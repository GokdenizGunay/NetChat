using NetChat.Core;
using NetChat.Models;
using NetChat.Net;

namespace NetChat.Services;

/// <summary>
/// Gruplar. Grup durumu tüm üyelerde saklanır; her üye bildiği en güncel durumu (son sürümlerin imzalı
/// geçmişiyle) çevrimiçi olan diğer üyelere aktarır. Böylece yöneticiler çevrimdışıyken de herkes güncel duruma
/// ulaşır. Yetki ve imza kuralları <see cref="GroupRules"/> içindedir.
/// </summary>
public sealed partial class ChatService
{
    private const int MaxGroupHistory = 20;

    private byte[]? PublicKeyOf(string id)
    {
        if (id == MyId) return _identity.PublicKey;
        try
        {
            return FindPeer(id)?.PublicKey is { Length: > 0 } pub ? Convert.FromBase64String(pub) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Yerel değişiklikler

    public Conversation CreateGroup(string name, string description, IReadOnlyCollection<Peer> members)
    {
        var state = new GroupState
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Description = description.Trim(),
            OwnerId = MyId,
            Admins = [MyId],
            Members = [new GroupMember { Id = MyId, Name = MyName }, .. members.Where(p => p.CanBeAddedToGroups).Select(p => new GroupMember { Id = p.Id, Name = p.Name })],
            Version = 1,
            CreatedAt = DateTimeOffset.Now,
        };
        var ev = new GroupEvent { Kind = GroupEventKind.Created, ActorId = MyId, ActorName = MyName };
        GroupRules.Sign(state, ev, _identity);
        var conv = new Conversation
        {
            Id = Conversation.GroupConversationId(state.Id),
            IsGroup = true,
            Title = state.Name,
            LastActivity = DateTimeOffset.Now,
        };
        Conversations.Add(conv);
        SetGroupState(conv, state);
        AddSystemMessage(conv, ev.Describe(MyId));
        BroadcastGroupState(state, ev, []);
        return conv;
    }

    /// <summary>Değişikliği çevrimiçi üyelere hemen gönderir; çevrimdışı olanlar bağlandıklarında herhangi bir üyeden alır.</summary>
    private void BroadcastGroupState(GroupState state, GroupEvent? ev, IEnumerable<string> extraRecipients)
    {
        var packet = new GroupStatePacket { Group = state.Clone(), Event = ev };
        var targets = state.Members.Select(m => m.Id).Concat(extraRecipients).Where(id => id != MyId).Distinct();
        foreach (var id in targets.Where(id => FindPeer(id)?.IsOnline == true))
            _ = _connections.SendAsync(id, packet);
    }

    /// <summary>Durumu kopyalar, değiştirir, sürümü artırır, imzalar ve tüm üyelere yayınlar.</summary>
    private bool ChangeGroup(Conversation conv, Func<GroupState, GroupEvent?> mutate, bool requireAdmin = true)
    {
        if (conv.Group is not { } current || current.Disbanded || !current.IsMember(MyId)) return false;
        if (requireAdmin && !current.IsAdmin(MyId)) return false;

        var next = current.Clone();
        var ev = mutate(next);
        if (ev == null) return false;
        ev.ActorId = MyId;
        ev.ActorName = MyName;
        next.Version = current.Version + 1;
        var removed = current.Members.Select(m => m.Id).Except(next.Members.Select(m => m.Id)).ToList();
        foreach (var id in removed)
        {
            next.Admins.Remove(id);
            next.Muted.Remove(id);
            if (!next.Removed.Contains(id)) next.Removed.Add(id);
        }
        if (next.Removed.Count > 100) next.Removed.RemoveRange(0, next.Removed.Count - 100);

        GroupRules.Sign(next, ev, _identity);
        SetGroupState(conv, next);
        AddSystemMessage(conv, ev.Describe(MyId));
        BroadcastGroupState(next, ev, removed);
        return true;
    }

    public bool CanEditGroupInfo(Conversation conv) =>
        conv.Group is { } g && !g.Disbanded && g.IsMember(MyId) && (!g.OnlyAdminsCanEdit || g.IsAdmin(MyId));

    public void AddMembers(Conversation conv, IReadOnlyCollection<Peer> peers) =>
        ChangeGroup(conv, g =>
        {
            var added = peers.Where(p => !g.IsMember(p.Id) && p.CanBeAddedToGroups).Select(p => new GroupMember { Id = p.Id, Name = p.Name }).ToList();
            if (added.Count == 0) return null;
            g.Members.AddRange(added);
            g.Removed.RemoveAll(id => added.Any(a => a.Id == id));
            return new GroupEvent { Kind = GroupEventKind.Added, Targets = added };
        });

    public void RemoveMember(Conversation conv, string memberId) =>
        ChangeGroup(conv, g =>
        {
            // Kurucu çıkarılamaz; yöneticileri yalnızca kurucu çıkarabilir.
            if (memberId == g.OwnerId || memberId == MyId) return null;
            if (g.Admins.Contains(memberId) && g.OwnerId != MyId) return null;
            var member = g.Members.FirstOrDefault(m => m.Id == memberId);
            if (member == null) return null;
            g.Members.Remove(member);
            return new GroupEvent { Kind = GroupEventKind.Removed, Targets = [member] };
        });

    public void SetAdmin(Conversation conv, string memberId, bool admin) =>
        ChangeGroup(conv, g =>
        {
            var member = g.Members.FirstOrDefault(m => m.Id == memberId);
            if (member == null || memberId == g.OwnerId) return null;
            if (admin)
            {
                if (g.Admins.Contains(memberId)) return null;
                g.Admins.Add(memberId);
                g.Muted.Remove(memberId);
                return new GroupEvent { Kind = GroupEventKind.Promoted, Targets = [member] };
            }
            // Yöneticiliği yalnızca kurucu geri alabilir.
            if (g.OwnerId != MyId || !g.Admins.Remove(memberId)) return null;
            return new GroupEvent { Kind = GroupEventKind.Demoted, Targets = [member] };
        });

    public void SetMuted(Conversation conv, string memberId, bool muted) =>
        ChangeGroup(conv, g =>
        {
            var member = g.Members.FirstOrDefault(m => m.Id == memberId);
            if (member == null || g.IsAdmin(memberId)) return null;
            if (muted)
            {
                if (g.Muted.Contains(memberId)) return null;
                g.Muted.Add(memberId);
                return new GroupEvent { Kind = GroupEventKind.Muted, Targets = [member] };
            }
            if (!g.Muted.Remove(memberId)) return null;
            return new GroupEvent { Kind = GroupEventKind.Unmuted, Targets = [member] };
        });

    public void RenameGroup(Conversation conv, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || conv.Group?.Name == name || !CanEditGroupInfo(conv)) return;
        ChangeGroup(conv, g =>
        {
            g.Name = name;
            return new GroupEvent { Kind = GroupEventKind.Renamed, Value = name };
        }, requireAdmin: false);
    }

    public void SetGroupDescription(Conversation conv, string description)
    {
        description = description.Trim();
        if (conv.Group?.Description == description || !CanEditGroupInfo(conv)) return;
        ChangeGroup(conv, g =>
        {
            g.Description = description;
            return new GroupEvent { Kind = GroupEventKind.DescriptionChanged };
        }, requireAdmin: false);
    }

    public void SetOnlyAdminsCanSend(Conversation conv, bool value) =>
        ChangeGroup(conv, g =>
        {
            if (g.OnlyAdminsCanSend == value) return null;
            g.OnlyAdminsCanSend = value;
            return new GroupEvent { Kind = GroupEventKind.SendPolicyChanged, Flag = value };
        });

    public void SetOnlyAdminsCanEdit(Conversation conv, bool value) =>
        ChangeGroup(conv, g =>
        {
            if (g.OnlyAdminsCanEdit == value) return null;
            g.OnlyAdminsCanEdit = value;
            return new GroupEvent { Kind = GroupEventKind.EditPolicyChanged, Flag = value };
        });

    public void DisbandGroup(Conversation conv)
    {
        if (conv.Group?.OwnerId != MyId) return;
        ChangeGroup(conv, g =>
        {
            g.Disbanded = true;
            return new GroupEvent { Kind = GroupEventKind.Disbanded };
        });
    }

    public void LeaveGroup(Conversation conv)
    {
        if (conv.Group is not { } g || !g.IsMember(MyId)) return;
        var ev = new GroupEvent { Kind = GroupEventKind.Left, ActorId = MyId, ActorName = MyName };
        var next = GroupRules.ApplyLeave(g, MyId);
        GroupRules.Sign(next, ev, _identity);
        // Giden kutusu: çevrimdışı üyeler de bağlandıklarında öğrenir.
        foreach (var id in Recipients(conv))
            Enqueue(id, new GroupStatePacket { Group = next.Clone(), Event = ev });
        SetGroupState(conv, next);
        AddSystemMessage(conv, ev.Describe(MyId));
    }

    /// <summary>
    /// Gizlilik ayarının izin vermediği bir ekleme: kendimi çıkardığım (imzalı) durumu üyelere gönderirim;
    /// gruba katılmam ve diğerleri neden eklenemediğimi görür.
    /// </summary>
    private void DeclineGroup(GroupState incoming)
    {
        var ev = new GroupEvent { Kind = GroupEventKind.Declined, ActorId = MyId, ActorName = MyName };
        var next = GroupRules.ApplyLeave(incoming, MyId);
        GroupRules.Sign(next, ev, _identity);
        foreach (var id in incoming.Members.Select(m => m.Id).Where(id => id != MyId))
            Enqueue(id, new GroupStatePacket { Group = next.Clone(), Event = ev });
        Log.Info("Grup daveti gizlilik ayarı nedeniyle reddedildi");
    }

    // ------------------------------------------------------------------ Yerel durum ve geçmiş

    /// <summary>Yerel durumu günceller ve imzalı sürümü geçmişe (son <see cref="MaxGroupHistory"/>) ekler.</summary>
    private void SetGroupState(Conversation conv, GroupState state)
    {
        conv.Group = state;
        conv.Title = state.Name;
        if (state.Signature != null)
        {
            conv.GroupHistory.RemoveAll(h => h.Version >= state.Version);
            conv.GroupHistory.Add(state.Clone());
            if (conv.GroupHistory.Count > MaxGroupHistory) conv.GroupHistory.RemoveRange(0, conv.GroupHistory.Count - MaxGroupHistory);
        }
        MarkDirty();
    }

    private static GroupState? StateAt(Conversation conv, long version) =>
        conv.GroupHistory.LastOrDefault(h => h.Version == version) ?? (conv.Group?.Version == version ? conv.Group : null);

    /// <summary>Bildiğim son durum ve geçmişi (yeni katılan ya da geride kalan üyenin eksiklerini tamamlaması için).</summary>
    private static GroupStatePacket SyncPacket(Conversation conv) => new()
    {
        Group = conv.Group!.Clone(),
        History = conv.GroupHistory.Select(h => h.Clone()).ToList(),
    };

    /// <summary>Bir eş çevrimiçi olunca: ortak olduğumuz grupların durumunu ona aktarırım (yönetici olmasam da).</summary>
    private async Task SyncGroupsAsync(string peerId)
    {
        foreach (var conv in Conversations.Where(c => c.IsGroup && c.Group != null).ToList())
        {
            var g = conv.Group!;
            if (g.IsMember(MyId) && (g.IsMember(peerId) || g.Removed.Contains(peerId)))
                await _connections.SendAsync(peerId, SyncPacket(conv));
        }
    }

    // ------------------------------------------------------------------ Gelen durumlar

    private void HandleGroupState(string peerId, GroupStatePacket p)
    {
        if (p.Group.Id == Guid.Empty) return;
        Log.Info($"Grup durumu alındı: v{p.Group.Version}, geçmiş {p.History?.Count ?? 0} (yerel v{FindGroup(p.Group.Id)?.Group?.Version})");
        // Önce geçmiş (eksik sürümler sırayla), sonra en güncel durum.
        if (p.History is { Count: > 0 } history)
            foreach (var h in history.Where(h => h.Id == p.Group.Id).OrderBy(h => h.Version))
                ApplyGroupState(h, null);
        ApplyGroupState(p.Group, p.Event);

        // Gönderen geride kalmışsa bildiğim güncel durumu ona aktarırım.
        var conv = FindGroup(p.Group.Id);
        if (conv?.Group is { } local && local.Version > p.Group.Version && local.IsMember(MyId)
            && (local.IsMember(peerId) || local.Removed.Contains(peerId)))
            _ = _connections.SendAsync(peerId, SyncPacket(conv));
    }

    private void ApplyGroupState(GroupState incoming, GroupEvent? ev)
    {
        var conv = FindGroup(incoming.Id);
        if (conv?.Group is not { } local)
        {
            AcceptInvite(incoming, ev);
            return;
        }

        if (incoming.Version <= local.Version)
        {
            // Aynı sürümde çakışma: tüm istemciler aynı durumu seçer.
            if (incoming.Version == local.Version && GroupRules.Wins(incoming, local)
                && StateAt(conv, incoming.Version - 1) is { } prev && GroupRules.IsValidTransition(prev, incoming, PublicKeyOf))
                ApplyNew(conv, local, incoming, ev ?? incoming.LastEvent);
            return;
        }

        var valid = incoming.Version == local.Version + 1
            ? GroupRules.IsValidTransition(local, incoming, PublicKeyOf)
            : GroupRules.IsValidJump(local, incoming, PublicKeyOf);
        if (!valid)
        {
            Log.Info($"Grup durumu reddedildi: v{incoming.Version} (yerel v{local.Version})");
            return;
        }

        if (!local.IsMember(MyId) && incoming.IsMember(MyId) && !incoming.Disbanded && !CanAddMeToGroups(incoming.SignerId!))
        {
            // Daha önce ayrıldığım gruba, izin vermediğim biri tarafından yeniden eklendim.
            SetGroupState(conv, incoming);
            DeclineGroup(incoming);
            return;
        }
        ApplyNew(conv, local, incoming, ev ?? incoming.LastEvent);
    }

    private void ApplyNew(Conversation conv, GroupState local, GroupState incoming, GroupEvent? ev)
    {
        var wasMember = local.IsMember(MyId);
        SetGroupState(conv, incoming);
        if (ev != null && ev.Describe(MyId) is { Length: > 0 } text) AddSystemMessage(conv, text);
        else if (wasMember && !incoming.IsMember(MyId)) AddSystemMessage(conv, "Artık bu grubun üyesi değilsiniz");
        else if (!local.Disbanded && incoming.Disbanded) AddSystemMessage(conv, "Grup dağıtıldı");
    }

    /// <summary>Yeni grup daveti (doğrudan ya da başka bir üyenin aktardığı geçmiş üzerinden).</summary>
    private void AcceptInvite(GroupState incoming, GroupEvent? ev)
    {
        if (!GroupRules.IsValidInvite(incoming, MyId, PublicKeyOf)) return;
        Log.Info($"Grup daveti kabul edildi: v{incoming.Version}");
        var adder = incoming.SignerId!;
        if (!CanAddMeToGroups(adder))
        {
            DeclineGroup(incoming);
            return;
        }
        var conv = new Conversation
        {
            Id = Conversation.GroupConversationId(incoming.Id),
            IsGroup = true,
            Title = incoming.Name,
            LastActivity = DateTimeOffset.Now,
        };
        Conversations.Add(conv);
        SetGroupState(conv, incoming);
        var shown = (ev ?? incoming.LastEvent) is { Kind: GroupEventKind.Created or GroupEventKind.Added } e
            ? e
            : new GroupEvent { Kind = GroupEventKind.Added, ActorId = adder, ActorName = incoming.MemberName(adder), Targets = [new GroupMember { Id = MyId, Name = MyName }] };
        AddSystemMessage(conv, shown.Describe(MyId));
        conv.UnreadCount++;
        UpdateTotalUnread();
    }
}
