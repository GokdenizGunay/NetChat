using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NetChat.Core;
using NetChat.Models;
using NetChat.Net;

namespace NetChat.Services;

/// <summary>
/// Uygulamanın mesajlaşma çekirdeği. Tüm durum (kişiler, sohbetler, mesajlar) UI iş parçacığında
/// tutulur ve doğrudan arayüze bağlanır. Ağdan gelen olaylar Dispatcher üzerinden buraya aktarılır.
/// </summary>
public sealed partial class ChatService : ObservableObject, IDisposable
{
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(14);
    private const long MaxIncomingFile = 4L * 1024 * 1024 * 1024;

    private readonly Identity _identity;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ConnectionManager _connections;
    private readonly DiscoveryService _discovery;
    private readonly ConcurrentDictionary<string, IPEndPoint> _endpoints = new();
    private readonly ConcurrentDictionary<Guid, IncomingFile> _incomingFiles = new();
    /// <summary>Engellenen kişiler (ağ iş parçacığından da okunur).</summary>
    private readonly ConcurrentDictionary<string, byte> _blockedIds = new();
    private readonly Dictionary<string, SemaphoreSlim> _sendGates = new();
    private readonly HashSet<(Guid, string)> _inFlight = new();
    private readonly Dictionary<string, DateTime> _typingSent = new();
    private readonly Dictionary<string, DateTime> _typingUntil = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _tickTimer;
    private bool _started;
    private int _tickCount;

    public ObservableCollection<Peer> Peers { get; } = [];
    public ObservableCollection<Conversation> Conversations { get; } = [];

    public string MyId => _identity.Id;
    public string MyName => _settings.DisplayName;
    public byte[] MyPublicKey => _identity.PublicKey;
    public int TcpPort => _connections.Port;

    /// <summary>Kullanıcının şu an baktığı sohbet (okundu bilgisi ve bildirimler için).</summary>
    [ObservableProperty] private Conversation? _activeConversation;

    /// <summary>Ana pencere görünür ve odakta mı.</summary>
    [ObservableProperty] private bool _isWindowActive;

    [ObservableProperty] private int _totalUnread;

    /// <summary>Bildirim gösterilmesi gereken yeni gelen mesaj.</summary>
    public event Action<Conversation, ChatMessage>? MessageNotification;

    /// <summary>Kullanıcıya gösterilecek kısa bilgi/hata metni.</summary>
    public event Action<string>? Info;

    private readonly Action<byte[]> _persist;

    /// <summary>Aynı profil ağda daha önce açılmış başka bir oturumda çalışıyor; bu oturum kapanmalı.</summary>
    public event Action? DuplicateSessionDetected;

    public ChatService(Identity identity, AppSettings settings, Dispatcher dispatcher, StoreData initial, Action<byte[]> persist)
    {
        _identity = identity;
        _settings = settings;
        _dispatcher = dispatcher;
        _persist = persist;
        _connections = new ConnectionManager(identity, () => _settings.DisplayName)
        {
            ResolveEndpoint = id => _endpoints.TryGetValue(id, out var ep) ? ep : null,
        };
        _discovery = new DiscoveryService(identity, () => _settings.DisplayName, () => _connections.Port, GetLocalPresence);

        _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SaveNow(), dispatcher);
        _saveTimer.Stop();
        _tickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        _tickTimer.Stop();

        LoadFromStore(initial);
    }

    // ------------------------------------------------------------------ Başlatma / kayıt

    private readonly List<OutboxItem> _outbox = [];
    private const int MaxSeenIds = 5000;
    private readonly HashSet<Guid> _seenIds = [];
    private readonly Queue<Guid> _seenOrder = new();

    private void RememberSeen(Guid id)
    {
        if (!_seenIds.Add(id)) return;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > MaxSeenIds) _seenIds.Remove(_seenOrder.Dequeue());
    }

    private void LoadFromStore(StoreData data)
    {
        foreach (var id in data.SeenMessageIds) RememberSeen(id);
        _outbox.AddRange(data.Outbox.Where(o => o.To.Length > 0 && o.Packet != null));
        foreach (var peer in data.Peers.Where(p => p.Id != MyId))
        {
            ApplyViewerPrivacy(peer);
            Peers.Add(peer);
            if (peer.IsBlocked) _blockedIds[peer.Id] = 0;
            if (peer.IpAddress != null && peer.Port > 0) _endpoints[peer.Id] = new IPEndPoint(peer.IpAddress, peer.Port);
        }
        RefreshDeviceSuffixes();
        foreach (var conv in data.Conversations)
        {
            if (!conv.IsGroup)
            {
                conv.Peer = FindPeer(conv.PeerId ?? conv.Id);
                if (conv.Peer != null) conv.Title = conv.Peer.Name;
            }
            foreach (var m in conv.Messages)
            {
                // Uygulama kapanırken yarım kalan aktarımlar.
                if (m.Transfer == TransferState.Transferring)
                    m.Transfer = m.IsOutgoing ? TransferState.Waiting : TransferState.Failed;
                if (m.Transfer == TransferState.Completed) m.Progress = 1;
            }
            conv.RefreshLayout(MyId);
            foreach (var m in conv.Messages.Where(m => m.Reactions.Count > 0)) m.RefreshReactions(MyId, id => NameOf(conv, id));
            Conversations.Add(conv);
        }
        UpdateTotalUnread();
    }

    /// <summary>
    /// Giriş ön kontrolü: ağı kendini duyurmadan dinler. Aynı profil başka bir yerde açıksa
    /// <see cref="PreflightResult.DuplicateSession"/>, aynı adla başka biri varsa <see cref="PreflightResult.NameTaken"/> döner.
    /// Sonuç olumluysa <see cref="JoinNetwork"/> ile ağa katılınır.
    /// </summary>
    public async Task<PreflightResult> StartAsync()
    {
        if (_started) return PreflightResult.Ok;
        _started = true;

        _connections.ChannelOpened += ch => Post(() => OnChannelOpened(ch));
        _connections.ChannelClosed += ch => Post(() => OnChannelClosed(ch));
        _connections.PacketReceived += OnPacketNetworkThread;
        _connections.ChunkReceived += OnChunkNetworkThread;
        _discovery.PeerSeen += p => Post(() => OnPeerSeen(p));
        _discovery.PeerLeft += id => Post(() => OnPeerLeft(id));
        _discovery.DuplicateSession += () => Post(() =>
        {
            _duplicate = true;
            if (_joined) DuplicateSessionDetected?.Invoke();
        });

        _connections.Start();
        try
        {
            _discovery.StartListening();
        }
        catch (Exception ex)
        {
            Log.Error("Keşif servisi başlatılamadı", ex);
            Info?.Invoke("Ağ keşfi başlatılamadı. Güvenlik duvarı ayarlarını kontrol edin.");
        }
        _settings.PropertyChanged += OnSettingsChanged;

        // Ağdaki diğer oturumların probe'a yanıt vermesi için kısa bir süre bekle.
        await Task.Delay(1800);
        if (_duplicate) return PreflightResult.DuplicateSession;
        return IsNameTaken(MyName) ? PreflightResult.NameTaken : PreflightResult.Ok;
    }

    /// <summary>Bu ad ağda başka biri tarafından kullanılıyor mu (büyük/küçük harf duyarsız).</summary>
    public bool IsNameTaken(string name) =>
        Peers.Any(p => p.IsOnline && p.Id != MyId &&
                       string.Equals(p.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Ön kontrol başarılı: kendini duyurur ve mesaj alışverişine başlar.</summary>
    public void JoinNetwork()
    {
        _joined = true;
        _discovery.BeginAnnouncing();
        _tickTimer.Start();
    }

    private bool _duplicate;
    private bool _joined;

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.DisplayName):
            case nameof(AppSettings.ShareOnlineStatus):
            case nameof(AppSettings.ShareLastActive):
                foreach (var peer in Peers) ApplyViewerPrivacy(peer);
                _ = _discovery.AnnounceAsync();
                break;
            case nameof(AppSettings.GroupAddMode):
            case nameof(AppSettings.GroupAddOnly):
            case nameof(AppSettings.GroupAddExcept):
            case nameof(AppSettings.Avatar):
                // Profil resmi değişince yeni parmak izi duyurulur; görenler yeni resmi kendileri ister.
                _ = _discovery.AnnounceAsync();
                break;
            case nameof(AppSettings.KeepMessageHistory):
                MarkDirty();
                break;
            case nameof(AppSettings.SendReadReceipts):
                // Okundu bilgisi karşılıklıdır: kapatan, bire bir sohbetlerde karşı tarafın okundu bilgisini de göremez.
                foreach (var conv in Conversations.Where(c => !c.IsGroup))
                    foreach (var m in conv.Messages.Where(m => m.IsOutgoing && !m.IsSystem))
                        UpdateStatus(conv, m);
                break;
        }
    }

    /// <summary>
    /// Diğer cihazlara duyurulan durum: pencere öndeyken çevrimiçi, arka plandayken/tepsideyken boşta.
    /// Uygulama tamamen kapandığında veya oturum kilitlendiğinde "bye" yayını ile çevrimdışı olur.
    /// Engellenen kişiler için durum ve son aktiflik gizlenir.
    /// </summary>
    private LocalPresence GetLocalPresence()
    {
        var state = !_settings.ShareOnlineStatus ? "hidden" : IsWindowActive ? "online" : "idle";
        long? lastActive = null;
        if (_settings.ShareLastActive)
            lastActive = (IsWindowActive ? DateTimeOffset.Now : _settings.LastActive ?? DateTimeOffset.Now).ToUnixTimeMilliseconds();
        var hideFrom = _blockedIds.Keys.Select(id => DiscoveryService.HideTag(MyId, id)).ToList();
        var (groupMode, groupList) = _settings.GroupAddMode switch
        {
            GroupAddMode.Nobody => ("none", null),
            GroupAddMode.Only => ("only", _settings.GroupAddOnly),
            GroupAddMode.Except => ("except", _settings.GroupAddExcept),
            _ => ((string?)null, (List<string>?)null),
        };
        var groupTags = groupList?.Select(id => DiscoveryService.GroupTag(MyId, id)).ToList();
        return new LocalPresence(state, lastActive, hideFrom, groupMode, groupTags, ProfilePicture.Hash(_settings.Avatar));
    }

    /// <summary>Gizlilik ayarına göre bu kişi beni gruplara ekleyebilir mi? (Engellediklerim hiçbir zaman ekleyemez.)</summary>
    public bool CanAddMeToGroups(string peerId)
    {
        if (_blockedIds.ContainsKey(peerId)) return false;
        return _settings.GroupAddMode switch
        {
            GroupAddMode.Nobody => false,
            GroupAddMode.Only => _settings.GroupAddOnly.Contains(peerId),
            GroupAddMode.Except => !_settings.GroupAddExcept.Contains(peerId),
            _ => true,
        };
    }
    public Task RescanAsync() => _discovery.ProbeAsync();

    public void ShowInfo(string text) => Info?.Invoke(text);

    private void Post(Action action) => _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);

    public void MarkDirty()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            _persist(ChatStore.Serialize(new StoreData
            {
                Peers = [.. PeersToSave()],
                Conversations = [.. Conversations],
                Outbox = [.. _outbox],
                SeenMessageIds = [.. _seenOrder],
            }, _settings.KeepMessageHistory));
        }
        catch (Exception ex)
        {
            Log.Error("Sohbetler kaydedilemedi", ex);
        }
    }

    /// <summary>
    /// Profilde yalnızca gerçekten gereken kişiler saklanır: bire bir sohbeti olanlar, grup üyeleri, engellenenler ve
    /// giden kutusunda işi bekleyenler. "Ağdaki kişiler" listesinin geçmişi tutulmaz.
    /// </summary>
    private IEnumerable<Peer> PeersToSave()
    {
        var needed = new HashSet<string>(_outbox.Select(o => o.To));
        foreach (var conv in Conversations)
        {
            if (conv.Group is { } g) needed.UnionWith(g.Members.Select(m => m.Id));
            else needed.Add(conv.PeerId ?? conv.Id);
        }
        return Peers.Where(p => p.IsBlocked || needed.Contains(p.Id));
    }

    private void Tick()
    {
        var now = DateTimeOffset.Now;

        // Çevrimiçi görünen ama o an ulaşılamayan eşlere bekleyen mesajları periyodik olarak yeniden dene.
        if (++_tickCount % 20 == 0)
        {
            var waiting = Conversations
                .SelectMany(c => c.Messages)
                .Where(m => m.IsOutgoing && !m.IsDeleted && m.PendingRecipients.Count > 0)
                .SelectMany(m => m.PendingRecipients)
                .Concat(_outbox.Select(o => o.To))
                .ToHashSet();
            foreach (var peer in Peers.Where(p => p.IsOnline && waiting.Contains(p.Id)))
                _ = FlushPendingAsync(peer.Id);
        }

        foreach (var peer in Peers)
        {
            if (peer.IsOnline && now - peer.LastSeen > OfflineAfter && !_connections.IsConnected(peer.Id))
                MarkOffline(peer);
        }
        // Gece yarısı geçince "dün" gibi göreli zamanlar güncellensin.
        if (_tickCount % 60 == 0)
            foreach (var peer in Peers) peer.RefreshPresenceText();
        foreach (var (convId, until) in _typingUntil.ToList())
        {
            if (DateTime.UtcNow < until) continue;
            _typingUntil.Remove(convId);
            var conv = FindConversation(convId);
            if (conv != null) conv.TypingText = null;
        }
    }

    // ------------------------------------------------------------------ Kişiler

    public Peer? FindPeer(string id) => Peers.FirstOrDefault(p => p.Id == id);

    public Conversation? FindConversation(string id) => Conversations.FirstOrDefault(c => c.Id == id);

    private Conversation? FindGroup(Guid groupId) => FindConversation(Conversation.GroupConversationId(groupId));

    private Peer UpsertPeer(string id, string name, string pub, IPAddress? address, int port)
    {
        var peer = FindPeer(id);
        if (peer == null)
        {
            peer = new Peer { Id = id, Name = name, PublicKey = pub };
            ApplyViewerPrivacy(peer);
            Peers.Add(peer);
        }
        if (!string.IsNullOrWhiteSpace(name) && peer.Name != name)
        {
            peer.Name = name;
            var dm = FindConversation(id);
            if (dm != null) dm.Title = name;
            MarkDirty();
        }
        if (string.IsNullOrEmpty(peer.PublicKey)) peer.PublicKey = pub;
        var addr = address != null && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        // Aynı makinedeki eşler hem LAN hem loopback üzerinden duyurulur; LAN adresini tercih et.
        if (addr != null && IPAddress.IsLoopback(addr) && peer.IpAddress is { } known && !IPAddress.IsLoopback(known))
            addr = known;
        if (addr != null && port > 0)
        {
            peer.Address = addr.ToString();
            peer.Port = port;
            _endpoints[id] = new IPEndPoint(addr, port);
        }
        peer.LastSeen = DateTimeOffset.Now;
        return peer;
    }

    private void SetOnline(Peer peer)
    {
        peer.SeenThisSession = true;
        if (peer.IsOnline) return;
        peer.IsOnline = true;
        Log.Info($"Çevrimiçi: {peer.Name}");
        _ = OnPeerCameOnlineAsync(peer.Id);
    }

    private async Task OnPeerCameOnlineAsync(string peerId)
    {
        // Önce grup durumları (gruba ait bekleyen mesajlar kabul edilebilsin), sonra bekleyen mesajlar.
        await SyncGroupsAsync(peerId);
        await FlushPendingAsync(peerId);
    }

    private void OnPeerSeen(DiscoveredPeer p)
    {
        var peer = UpsertPeer(p.Id, p.Name, p.PublicKey, p.Address, p.Port);
        peer.SeenThisSession = true;
        peer.BlocksMe = p.HidesFromMe;
        peer.AllowsGroupAdd = p.CanAddMe;
        // Eski sürümler cihaz kodu ve profil resmi göndermez; onlar için hiçbir şey değişmez.
        if (p.DeviceCode != null)
        {
            if (peer.DeviceCode != p.DeviceCode)
            {
                peer.DeviceCode = p.DeviceCode;
                RefreshDeviceSuffixes();
                MarkDirty();
            }
            SyncAvatar(peer, p.AvatarHash);
        }
        // Engellenen ya da bizi engelleyen kişinin durum/son aktifliği güncellenmez.
        if (!peer.IsBlocked && !p.HidesFromMe) ApplyPresence(peer, p.State, p.LastActive);
        SetOnline(peer);
    }

    /// <summary>Durum/son aktiflik karşılıklıdır: kendininkini gizleyen başkalarınınkini de göremez.</summary>
    private void ApplyViewerPrivacy(Peer peer)
    {
        peer.ViewerSharesStatus = _settings.ShareOnlineStatus;
        peer.ViewerSharesLastActive = _settings.ShareLastActive;
    }

    private void ApplyPresence(Peer peer, string? state, long? lastActiveMs)
    {
        // Eski sürümler durum göndermez; onları çevrimiçi kabul et.
        peer.SharesStatus = state != "hidden";
        peer.IsActive = state is null or "online";
        DateTimeOffset? lastActive = lastActiveMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime() : null;
        // Dakika hassasiyetinde sakla: çevrimiçi kişi her duyuruda "şimdi" bildirir; her seferinde diske yazılmasın.
        if (lastActive is { } la) lastActive = new DateTimeOffset(la.Year, la.Month, la.Day, la.Hour, la.Minute, 0, la.Offset);
        if (peer.LastActive != lastActive)
        {
            peer.LastActive = lastActive;
            MarkDirty();
        }
    }

    private void MarkOffline(Peer peer)
    {
        // Çevrimiçiyken kaybolan kişinin son aktifliği kaybolduğu an olur (paylaşıyorsa).
        if (peer.IsActive && peer.LastActive != null) peer.LastActive = DateTimeOffset.Now;
        peer.IsActive = false;
        peer.IsOnline = false;
        MarkDirty();
    }

    private void OnPeerLeft(string id)
    {
        var peer = FindPeer(id);
        if (peer == null) return;
        MarkOffline(peer);
        peer.LastSeen = DateTimeOffset.Now;
        _connections.Disconnect(id);
    }

    // ------------------------------------------------------------------ Engelleme / sessize alma

    public void SetBlocked(Peer peer, bool blocked)
    {
        if (peer.IsBlocked == blocked) return;
        peer.IsBlocked = blocked;
        if (blocked) _blockedIds[peer.Id] = 0; else _blockedIds.TryRemove(peer.Id, out _);
        var dm = FindConversation(peer.Id);
        if (dm != null)
        {
            dm.TypingText = null;
            if (blocked)
                foreach (var m in dm.Messages.Where(m => m.IsOutgoing && m.PendingRecipients.Count > 0))
                    m.PendingRecipients.Clear();
            AddSystemMessage(dm, blocked ? "Bu kişiyi engellediniz" : "Bu kişinin engelini kaldırdınız");
        }
        BlockChanged?.Invoke(peer);
        // Engellenen kişiye durum ve son aktiflik bilgimiz artık gösterilmez (yeni duyuruda etiketlenir).
        _ = _discovery.AnnounceAsync();
        MarkDirty();
    }

    /// <summary>Bir kişinin engel durumu değişti (arayüz yazma alanını güncellesin).</summary>
    public event Action<Peer>? BlockChanged;

    public void SetMuted(Conversation conv, bool muted)
    {
        conv.IsMuted = muted;
        MarkDirty();
    }

    private void OnChannelOpened(SecureChannel ch)
    {
        var port = ch.IsInitiator ? 0 : ch.PeerHello.Port;
        var peer = UpsertPeer(ch.PeerId, ch.PeerHello.Name, ch.PeerHello.Pub, port > 0 ? ch.RemoteAddress : null, port);
        SetOnline(peer);
    }

    /// <summary>
    /// Yöneticisi olduğumuz gruplarda, yeni çevrimiçi olan eşin güncel grup durumunu almasını sağlar
    /// (çevrimdışıyken kaçırdığı davet, çıkarma veya ayar değişiklikleri için).
    /// </summary>
    private void OnChannelClosed(SecureChannel ch)
    {
        // Bu eşten yarım kalan dosyalar: gönderen yeniden bağlandığında baştan gönderir.
        foreach (var file in _incomingFiles.Values.Where(f => f.PeerId == ch.PeerId).ToList())
        {
            if (!_incomingFiles.TryRemove(file.MessageId, out _)) continue;
            file.Abort();
            var msg = FindMessage(file.ConversationId, file.MessageId);
            if (msg != null && msg.Transfer != TransferState.Completed) msg.Transfer = TransferState.Waiting;
        }
    }

    // ------------------------------------------------------------------ Sohbetler

    public Conversation OpenDirect(Peer peer)
    {
        var conv = FindConversation(peer.Id);
        if (conv != null) return conv;
        conv = new Conversation
        {
            Id = peer.Id,
            PeerId = peer.Id,
            Peer = peer,
            Title = peer.Name,
            LastActivity = DateTimeOffset.Now,
        };
        conv.RefreshLayout(MyId);
        Conversations.Add(conv);
        MarkDirty();
        return conv;
    }

    private Conversation GetOrCreateDirect(string peerId)
    {
        var peer = FindPeer(peerId) ?? UpsertPeer(peerId, "Bilinmeyen", "", null, 0);
        return OpenDirect(peer);
    }

    private static ChatMessage? FindMessage(Conversation? conv, Guid id) =>
        conv?.Messages.FirstOrDefault(m => m.Id == id);

    private ChatMessage? FindMessage(string convId, Guid id) => FindMessage(FindConversation(convId), id);

    private void AddMessage(Conversation conv, ChatMessage msg)
    {
        msg.ConversationId = conv.Id;
        conv.Messages.Add(msg);
        conv.LastActivity = msg.Timestamp > conv.LastActivity ? msg.Timestamp : DateTimeOffset.Now;
        conv.RefreshLayout(MyId, conv.Messages.Count - 1);
        MarkDirty();
    }

    private void AddSystemMessage(Conversation conv, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        AddMessage(conv, new ChatMessage
        {
            Id = Guid.NewGuid(),
            Kind = MessageKind.System,
            Text = text,
            Timestamp = DateTimeOffset.Now,
            AnimateIn = true,
        });
    }

    public void DeleteConversation(Conversation conv)
    {
        if (conv.IsGroup && conv.Group is { } g && g.IsMember(MyId) && !g.Disbanded)
            LeaveGroup(conv);
        Conversations.Remove(conv);
        if (ActiveConversation == conv) ActiveConversation = null;
        UpdateTotalUnread();
        MarkDirty();
    }

    public void ClearConversation(Conversation conv)
    {
        conv.Messages.Clear();
        conv.UnreadCount = 0;
        conv.RefreshLayout(MyId);
        UpdateTotalUnread();
        MarkDirty();
    }

    private IEnumerable<string> Recipients(Conversation conv) =>
        conv.IsGroup
            ? conv.Group?.Members.Select(m => m.Id).Where(id => id != MyId) ?? []
            : [conv.PeerId ?? conv.Id];

    /// <summary>Kullanıcının bu sohbete mesaj gönderip gönderemeyeceği; gönderemiyorsa nedeni.</summary>
    public string? SendBlockedReason(Conversation conv)
    {
        if (!conv.IsGroup) return conv.Peer?.IsBlocked == true ? "Bu kişiyi engellediniz. Mesaj göndermek için engeli kaldırın." : null;
        if (conv.Group is not { } g) return null;
        if (g.Disbanded) return "Bu grup dağıtıldı.";
        if (!g.IsMember(MyId)) return "Artık bu grubun üyesi değilsiniz.";
        if (g.IsMuted(MyId)) return "Bir yönetici tarafından susturuldunuz.";
        if (g.OnlyAdminsCanSend && !g.IsAdmin(MyId)) return "Bu gruba yalnızca yöneticiler mesaj gönderebilir.";
        return null;
    }

    // ------------------------------------------------------------------ Gönderme

    public void SendText(Conversation conv, string text, ChatMessage? replyTo = null)
    {
        text = text.Trim();
        if (text.Length == 0 || SendBlockedReason(conv) != null) return;
        if (text.Length > 20000) text = text[..20000];
        var msg = NewOutgoing(conv, MessageKind.Text);
        msg.Text = text;
        ApplyReply(msg, replyTo);
        if (conv.Group is { } g) msg.Mentions = FindMentions(g, text);
        AddMessage(conv, msg);
        _ = DeliverAsync(conv, msg);
    }

    private static void ApplyReply(ChatMessage msg, ChatMessage? replyTo)
    {
        if (replyTo == null || replyTo.IsSystem) return;
        msg.ReplyToId = replyTo.Id;
        msg.ReplySenderId = replyTo.SenderId;
        msg.ReplySenderName = replyTo.SenderName;
        var preview = replyTo.PreviewText;
        msg.ReplyText = preview.Length > 160 ? preview[..160] + "…" : preview;
    }

    /// <summary>Metinde "@Ad" olarak geçen grup üyelerini bulur (uzun adlar önce, böylece "Ali Kaya" "Ali"den önce eşleşir).</summary>
    public List<GroupMember> FindMentions(GroupState g, string text) =>
        g.Members
            .Where(m => m.Id != MyId && !string.IsNullOrWhiteSpace(m.Name))
            .OrderByDescending(m => m.Name.Length)
            .Where(m => text.Contains("@" + m.Name, StringComparison.CurrentCultureIgnoreCase))
            .Select(m => new GroupMember { Id = m.Id, Name = m.Name })
            .ToList();

    public void SendFile(Conversation conv, string path, ChatMessage? replyTo = null)
    {
        if (SendBlockedReason(conv) != null) return;
        var info = new FileInfo(path);
        if (!info.Exists) return;
        if (info.Length > MaxIncomingFile)
        {
            Info?.Invoke($"{info.Name} çok büyük (en fazla 4 GB).");
            return;
        }
        var msg = NewOutgoing(conv, MessageKind.File);
        msg.FileName = info.Name;
        msg.FileSize = info.Length;
        msg.LocalPath = info.FullName;
        msg.Transfer = TransferState.Waiting;
        ApplyReply(msg, replyTo);
        AddMessage(conv, msg);
        _ = DeliverAsync(conv, msg);
    }

    private ChatMessage NewOutgoing(Conversation conv, MessageKind kind)
    {
        var msg = new ChatMessage
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            SenderId = MyId,
            SenderName = MyName,
            Timestamp = DateTimeOffset.Now,
            IsOutgoing = true,
            Status = MessageStatus.Pending,
            AnimateIn = true,
        };
        foreach (var r in Recipients(conv)) msg.PendingRecipients.Add(r);
        if (msg.PendingRecipients.Count == 0) msg.Status = MessageStatus.Sent;
        return msg;
    }

    private async Task DeliverAsync(Conversation conv, ChatMessage msg)
    {
        var tasks = msg.PendingRecipients.ToList()
            .Where(id => FindPeer(id)?.IsOnline == true)
            .Select(id => DeliverToAsync(conv, msg, id));
        await Task.WhenAll(tasks);
    }

    private SemaphoreSlim SendGate(string peerId)
    {
        if (!_sendGates.TryGetValue(peerId, out var gate)) _sendGates[peerId] = gate = new SemaphoreSlim(1, 1);
        return gate;
    }

    /// <summary>Mesajı tek bir alıcıya iletir (UI iş parçacığında çağrılır).</summary>
    private async Task DeliverToAsync(Conversation conv, ChatMessage msg, string peerId)
    {
        if (!_inFlight.Add((msg.Id, peerId))) return;
        var gate = SendGate(peerId);
        await gate.WaitAsync();
        try
        {
            if (msg.IsDeleted || !msg.PendingRecipients.Contains(peerId)) return;
            if (!conv.IsGroup && FindPeer(peerId)?.IsBlocked == true) return;

            var packet = new ChatPacket
            {
                MessageId = msg.Id,
                GroupId = conv.IsGroup ? conv.Group?.Id : null,
                Kind = msg.Kind,
                Text = msg.Text,
                Timestamp = msg.Timestamp,
                SenderName = MyName,
                FileName = msg.FileName,
                FileSize = msg.FileSize,
                ReplyToId = msg.ReplyToId,
                ReplySenderId = msg.ReplySenderId,
                ReplySenderName = msg.ReplySenderName,
                ReplyText = msg.ReplyText,
                Mentions = msg.Mentions.Count > 0 ? msg.Mentions : null,
            };

            bool ok;
            if (msg.Kind == MessageKind.File)
            {
                if (msg.LocalPath == null || !File.Exists(msg.LocalPath))
                {
                    msg.Transfer = TransferState.Failed;
                    msg.Status = MessageStatus.Failed;
                    msg.PendingRecipients.Clear();
                    MarkDirty();
                    return;
                }
                msg.Transfer = TransferState.Transferring;
                msg.Progress = 0;
                ok = await _connections.SendAsync(peerId, packet) &&
                     await Task.Run(() => StreamFileAsync(peerId, msg, msg.LocalPath));
                msg.Transfer = ok || msg.Transfer == TransferState.Completed ? TransferState.Completed : TransferState.Waiting;
                if (ok) msg.Progress = 1;
            }
            else
            {
                ok = await _connections.SendAsync(peerId, packet);
            }

            if (ok)
            {
                // Bağlantıya yazılması alıcının aldığı anlamına gelmez (bağlantı o an kapanıyor olabilir).
                // Alıcı bekleyenlerde kalır; "iletildi" onayı gelince çıkarılır, gelmezse yeniden gönderilir.
                if (msg.Status is MessageStatus.Pending or MessageStatus.Failed) msg.Status = MessageStatus.Sent;
                MarkDirty();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mesaj iletilemedi", ex);
        }
        finally
        {
            gate.Release();
            _inFlight.Remove((msg.Id, peerId));
        }
    }

    /// <summary>Dosyayı parçalar halinde gönderir (arka plan iş parçacığında).</summary>
    private async Task<bool> StreamFileAsync(string peerId, ChatMessage msg, string path)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                Wire.ChunkSize, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[Wire.ChunkSize];
            long offset = 0;
            var total = Math.Max(1, fs.Length);
            var lastReport = DateTime.MinValue;
            int read;
            while ((read = await fs.ReadAsync(buffer)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                if (!await _connections.SendRawAsync(peerId, Wire.EncodeChunk(msg.Id, offset, buffer.AsSpan(0, read))))
                    return false;
                offset += read;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(120))
                {
                    lastReport = DateTime.UtcNow;
                    var progress = (double)offset / total;
                    Post(() => msg.Progress = progress);
                }
            }
            var hash = Convert.ToHexString(sha.GetHashAndReset());
            return await _connections.SendAsync(peerId, new FileDonePacket { MessageId = msg.Id, Sha256 = hash });
        }
        catch (Exception ex)
        {
            Log.Error("Dosya gönderilemedi", ex);
            return false;
        }
    }

    /// <summary>Eş çevrimiçi olduğunda bekleyen mesajları sırayla gönderir.</summary>
    private async Task FlushPendingAsync(string peerId)
    {
        var pending = Conversations
            .SelectMany(c => c.Messages.Where(m => m.IsOutgoing && !m.IsDeleted && m.PendingRecipients.Contains(peerId))
                .Select(m => (Conv: c, Msg: m)))
            .OrderBy(x => x.Msg.Timestamp)
            .ToList();
        foreach (var (conv, msg) in pending)
            await DeliverToAsync(conv, msg, peerId);
        await FlushOutboxAsync(peerId);
    }

    /// <summary>
    /// Paketi giden kutusuna koyar (herkesten silme, ifade, okundu, grup ayrılma/ret); alıcı çevrimiçiyse
    /// hemen, değilse bağlandığında gönderilir.
    /// Aynı mesaja verilen ifadelerden yalnızca sonuncusu saklanır.
    /// </summary>
    private void Enqueue(string to, Packet packet)
    {
        if (to == MyId) return;
        if (packet is ReactionPacket reaction)
            _outbox.RemoveAll(o => o.To == to && o.Packet is ReactionPacket r && r.MessageId == reaction.MessageId);
        _outbox.Add(new OutboxItem { To = to, Packet = packet });
        MarkDirty();
        if (FindPeer(to)?.IsOnline == true) _ = FlushOutboxAsync(to);
    }

    private async Task FlushOutboxAsync(string peerId)
    {
        foreach (var item in _outbox.Where(o => o.To == peerId).ToList())
        {
            if (!_outbox.Contains(item)) continue;
            if (!await _connections.SendAsync(peerId, item.Packet)) return;
            _outbox.Remove(item);
            MarkDirty();
        }
    }

    public void RetryMessage(ChatMessage msg)
    {
        var conv = FindConversation(msg.ConversationId);
        if (conv == null) return;
        if (msg.PendingRecipients.Count == 0)
            foreach (var r in Recipients(conv)) msg.PendingRecipients.Add(r);
        msg.Status = MessageStatus.Pending;
        _ = DeliverAsync(conv, msg);
    }

    public void NotifyTyping(Conversation conv)
    {
        if (SendBlockedReason(conv) != null) return;
        var now = DateTime.UtcNow;
        if (_typingSent.TryGetValue(conv.Id, out var last) && now - last < TimeSpan.FromSeconds(3)) return;
        _typingSent[conv.Id] = now;
        var packet = new TypingPacket { GroupId = conv.IsGroup ? conv.Group?.Id : null };
        foreach (var id in Recipients(conv).Where(id => FindPeer(id)?.IsOnline == true))
            _ = _connections.SendAsync(id, packet);
    }

    // ------------------------------------------------------------------ Silme

    public bool CanDeleteForEveryone(ChatMessage msg)
    {
        if (msg.IsDeleted || msg.IsSystem) return false;
        if (msg.IsOutgoing) return true;
        var conv = FindConversation(msg.ConversationId);
        return conv?.IsGroup == true && conv.Group is { } g && g.IsAdmin(MyId) && g.IsMember(MyId);
    }

    public void DeleteForMe(ChatMessage msg)
    {
        var conv = FindConversation(msg.ConversationId);
        if (conv == null) return;
        var index = conv.Messages.IndexOf(msg);
        if (index < 0) return;
        conv.Messages.RemoveAt(index);
        conv.RefreshLayout(MyId, index);
        MarkDirty();
    }

    public void DeleteForEveryone(ChatMessage msg)
    {
        var conv = FindConversation(msg.ConversationId);
        if (conv == null || !CanDeleteForEveryone(msg)) return;
        MarkDeleted(conv, msg);
        // Giden kutusu: çevrimdışı alıcılara bağlandıklarında gönderilir (mesaj onlarda kalmasın).
        foreach (var id in Recipients(conv))
            Enqueue(id, new DeletePacket { MessageId = msg.Id, GroupId = conv.IsGroup ? conv.Group?.Id : null });
    }

    private void MarkDeleted(Conversation conv, ChatMessage msg)
    {
        msg.IsDeleted = true;
        msg.Text = null;
        msg.PendingRecipients.Clear();
        if (!msg.IsOutgoing && msg.Kind == MessageKind.File && msg.Transfer != TransferState.Completed)
            msg.Transfer = TransferState.Failed;
        conv.RefreshPreview(MyId);
        MarkDirty();
    }

    // ------------------------------------------------------------------ Okundu bilgisi

    public void MarkConversationRead(Conversation conv)
    {
        var unread = conv.Messages.Where(m => !m.IsOutgoing && !m.IsRead && !m.IsSystem).ToList();
        foreach (var m in unread) m.IsRead = true;
        if (conv.UnreadCount != 0)
        {
            conv.UnreadCount = 0;
            UpdateTotalUnread();
        }
        if (unread.Count == 0) return;
        MarkDirty();
        // Gruplarda okundu bilgisi her zaman gönderilir; bire bir sohbetlerde ayara bağlıdır.
        if (!conv.IsGroup && !_settings.SendReadReceipts) return;
        foreach (var bySender in unread.GroupBy(m => m.SenderId))
        {
            var packet = new AckPacket
            {
                Kind = AckKind.Read,
                GroupId = conv.IsGroup ? conv.Group?.Id : null,
                MessageIds = bySender.Select(m => m.Id).ToList(),
            };
            Enqueue(bySender.Key, packet);
        }
    }

    public void MarkConversationUnread(Conversation conv)
    {
        conv.UnreadCount = Math.Max(1, conv.UnreadCount);
        UpdateTotalUnread();
    }

    private void UpdateTotalUnread() => TotalUnread = Conversations.Sum(c => c.UnreadCount);

    private bool IsViewing(Conversation conv) => IsWindowActive && ActiveConversation == conv;

    partial void OnActiveConversationChanged(Conversation? value)
    {
        if (value != null && IsWindowActive) MarkConversationRead(value);
    }

    partial void OnIsWindowActiveChanged(bool value)
    {
        if (value && ActiveConversation != null) MarkConversationRead(ActiveConversation);
        // Pencere arkaya düştüğü an son aktiflik olur; durumu hemen duyur (çevrimiçi ↔ boşta).
        if (!value) _settings.LastActive = DateTimeOffset.Now;
        if (_started) _ = _discovery.AnnounceAsync();
    }

    // ------------------------------------------------------------------ Gelen paketler

    /// <summary>Ağ iş parçacığında çalışır. Dosya kayıtları burada yapılır ki parçalar kaçmasın.</summary>
    private void OnPacketNetworkThread(SecureChannel ch, Packet packet)
    {
        switch (packet)
        {
            case ChatPacket { Kind: MessageKind.File } cp:
                RegisterIncomingFile(ch.PeerId, cp);
                break;
            case FileDonePacket done:
                FinishIncomingFile(ch.PeerId, done);
                return;
            case FileCancelPacket cancel:
                if (_incomingFiles.TryRemove(cancel.MessageId, out var f) && f.PeerId == ch.PeerId) f.Abort();
                return;
        }
        var peerId = ch.PeerId;
        Post(() => HandlePacket(peerId, packet));
    }

    private void HandlePacket(string peerId, Packet packet)
    {
        var peer = FindPeer(peerId);
        if (peer != null)
        {
            peer.LastSeen = DateTimeOffset.Now;
            SetOnline(peer);
        }
        try
        {
            switch (packet)
            {
                case ChatPacket p: HandleChat(peerId, p); break;
                case AckPacket p: HandleAck(peerId, p); break;
                case TypingPacket p: HandleTyping(peerId, p); break;
                case DeletePacket p: HandleDelete(peerId, p); break;
                case GroupStatePacket p: HandleGroupState(peerId, p); break;
                case ReactionPacket p: HandleReaction(peerId, p); break;
                case AvatarRequestPacket: HandleAvatarRequest(peerId); break;
                case AvatarPacket p: HandleAvatar(peerId, p); break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Paket işlenemedi ({packet.GetType().Name})", ex);
        }
    }

    // ------------------------------------------------------------------ Cihaz etiketleri

    /// <summary>
    /// Ad etiketleri ("elaa#5543"): bilinen tüm cihaz kodları (bizimki dahil) arasında numara çakışırsa yetecek kadar
    /// harf eklenir ("elaaT#5543").
    /// </summary>
    private void RefreshDeviceSuffixes()
    {
        var suffixes = DeviceCode.Suffixes(Peers.Select(p => p.DeviceCode).OfType<string>().Append(DeviceCode.Current));
        foreach (var peer in Peers)
            peer.DeviceSuffix = peer.DeviceCode is { } code && suffixes.TryGetValue(code, out var s) ? s : null;
    }

    // ------------------------------------------------------------------ Profil resimleri

    /// <summary>Son istenen parmak izi ve zamanı: aynı resim her duyuruda yeniden istenmesin.</summary>
    private readonly Dictionary<string, (string Hash, DateTime At)> _avatarRequests = [];

    private void SyncAvatar(Peer peer, string? hash)
    {
        if (hash == peer.AvatarHash) return;
        if (hash == null)
        {
            peer.Avatar = null;
            MarkDirty();
            return;
        }
        // Bizi engelleyen ya da bizim engellediğimiz kişiden resim istenmez.
        if (peer.BlocksMe || peer.IsBlocked) return;
        if (_avatarRequests.TryGetValue(peer.Id, out var last) && last.Hash == hash && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(30)) return;
        _avatarRequests[peer.Id] = (hash, DateTime.UtcNow);
        _ = _connections.SendAsync(peer.Id, new AvatarRequestPacket());
    }

    private void HandleAvatarRequest(string peerId)
    {
        // Engellenen kişi resmimizi alamaz.
        if (FindPeer(peerId)?.IsBlocked == true) return;
        _ = _connections.SendAsync(peerId, new AvatarPacket { Data = _settings.Avatar });
    }

    private void HandleAvatar(string peerId, AvatarPacket p)
    {
        if (FindPeer(peerId) is not { } peer) return;
        _avatarRequests.Remove(peerId);
        var data = p.Data is { Length: > 0 } d && ProfilePicture.IsValid(d) ? d : null;
        if (data == peer.Avatar) return;
        peer.Avatar = data;
        MarkDirty();
    }

    private Conversation? ResolveConversation(string peerId, Guid? groupId)
    {
        if (groupId == null) return GetOrCreateDirect(peerId);
        var conv = FindGroup(groupId.Value);
        return conv?.Group != null && conv.Group.IsMember(peerId) ? conv : null;
    }

    private void HandleChat(string peerId, ChatPacket p)
    {
        if (p.Kind == MessageKind.System) return;
        // Engellenen kişinin bire bir mesajları sessizce yok sayılır (iletildi bilgisi de gönderilmez).
        if (p.GroupId == null && FindPeer(peerId)?.IsBlocked == true) return;
        var conv = ResolveConversation(peerId, p.GroupId);
        if (conv == null) return;
        if (conv.IsGroup && (conv.Group == null || !conv.Group.CanSend(peerId) || !conv.Group.IsMember(MyId))) return;

        var existing = FindMessage(conv, p.MessageId);
        if (existing != null)
        {
            // Tekrar gönderim (ör. dosya aktarımı yarıda kalmıştı).
            if (existing.Kind == MessageKind.File && existing.Transfer != TransferState.Completed)
                existing.Transfer = TransferState.Transferring;
            SendAck(peerId, AckKind.Delivered, p.GroupId, existing.Id);
            return;
        }
        if (_seenIds.Contains(p.MessageId))
        {
            // Daha önce alınmış, geçmiş tutulmadığı için silinmiş mesaj: yalnızca onayı yinele.
            SendAck(peerId, AckKind.Delivered, p.GroupId, p.MessageId);
            return;
        }
        RememberSeen(p.MessageId);

        var peerName = FindPeer(peerId)?.Name ?? p.SenderName;
        var msg = new ChatMessage
        {
            Id = p.MessageId,
            Kind = p.Kind,
            SenderId = peerId,
            SenderName = string.IsNullOrWhiteSpace(peerName) ? p.SenderName : peerName,
            Timestamp = p.Timestamp,
            Text = p.Text,
            AnimateIn = true,
            ReplyToId = p.ReplyToId,
            ReplySenderId = p.ReplySenderId,
            ReplySenderName = p.ReplySenderName,
            ReplyText = p.ReplyText,
            Mentions = p.Mentions ?? [],
        };
        var mentionsMe = conv.IsGroup && msg.Mentions.Any(m => m.Id == MyId);
        if (p.Kind == MessageKind.File)
        {
            msg.FileName = SanitizeFileName(p.FileName);
            msg.FileSize = p.FileSize;
            msg.Transfer = _incomingFiles.ContainsKey(p.MessageId) ? TransferState.Transferring : TransferState.Failed;
            if (_incomingFiles.TryGetValue(p.MessageId, out var file)) file.ConversationId = conv.Id;
        }

        AddMessage(conv, msg);
        SendAck(peerId, AckKind.Delivered, p.GroupId, msg.Id);

        if (IsViewing(conv))
        {
            MarkConversationRead(conv);
        }
        else
        {
            conv.UnreadCount++;
            UpdateTotalUnread();
        }

        if (_typingUntil.Remove(conv.Id)) conv.TypingText = null;

        // Sessize alınmış sohbetlerde bildirim yok; grupta etiketlendiyseniz yine de bildirilir.
        if (!IsViewing(conv) && (!conv.IsMuted || mentionsMe)) MessageNotification?.Invoke(conv, msg);
    }

    private void SendAck(string peerId, AckKind kind, Guid? groupId, Guid messageId) =>
        _ = _connections.SendAsync(peerId, new AckPacket { Kind = kind, GroupId = groupId, MessageIds = [messageId] });

    private void HandleAck(string peerId, AckPacket p)
    {
        var conv = p.GroupId is { } gid ? FindGroup(gid) : FindConversation(peerId);
        if (conv == null) return;
        var ids = p.MessageIds.ToHashSet();
        foreach (var msg in conv.Messages.Where(m => m.IsOutgoing && ids.Contains(m.Id)))
        {
            msg.PendingRecipients.Remove(peerId);
            msg.DeliveredTo.Add(peerId);
            if (p.Kind == AckKind.Read) msg.ReadBy.Add(peerId);
            UpdateStatus(conv, msg);
        }
        MarkDirty();
    }

    /// <summary>
    /// Durumu iletim/okunma kayıtlarından yeniden hesaplar. Okundu bilgisi karşılıklıdır: kullanıcı okundu
    /// bilgisini kapattıysa bire bir sohbetlerde karşı tarafın okuduğunu da göremez (en fazla "iletildi").
    /// Gruplarda okundu bilgisi her zaman görünür.
    /// </summary>
    private void UpdateStatus(Conversation conv, ChatMessage msg)
    {
        if (msg.IsSystem || msg.Status == MessageStatus.Failed) return;
        var recipients = Recipients(conv).ToList();
        if (recipients.Count == 0) return;
        var readVisible = conv.IsGroup || _settings.SendReadReceipts;
        MessageStatus status;
        if (readVisible && recipients.All(msg.ReadBy.Contains)) status = MessageStatus.Read;
        else if (recipients.All(r => msg.DeliveredTo.Contains(r) || msg.ReadBy.Contains(r))) status = MessageStatus.Delivered;
        else if (msg.DeliveredTo.Count > 0 || msg.PendingRecipients.Count < recipients.Count || msg.Status == MessageStatus.Sent) status = MessageStatus.Sent;
        else status = MessageStatus.Pending;
        msg.Status = status;
    }

    // ------------------------------------------------------------------ İfadeler

    /// <summary>Mesaja ifade bırakır; aynı ifade tekrar seçilirse kaldırılır.</summary>
    public void React(ChatMessage msg, string emoji)
    {
        var conv = FindConversation(msg.ConversationId);
        if (conv == null || msg.IsSystem || msg.IsDeleted || SendBlockedReason(conv) != null) return;
        string? value = msg.Reactions.TryGetValue(MyId, out var current) && current == emoji ? null : emoji;
        SetReaction(conv, msg, MyId, value);
        foreach (var id in Recipients(conv))
            Enqueue(id, new ReactionPacket { MessageId = msg.Id, GroupId = conv.IsGroup ? conv.Group?.Id : null, Emoji = value });
    }

    private void SetReaction(Conversation conv, ChatMessage msg, string userId, string? emoji)
    {
        if (emoji == null) msg.Reactions.Remove(userId);
        else msg.Reactions[userId] = emoji.Length > 32 ? emoji[..32] : emoji;
        msg.RefreshReactions(MyId, id => NameOf(conv, id));
        MarkDirty();
    }

    private string NameOf(Conversation conv, string id) =>
        FindPeer(id)?.Name ?? conv.Group?.MemberName(id) ?? "Bilinmeyen";

    private void HandleReaction(string peerId, ReactionPacket p)
    {
        if (p.GroupId == null && FindPeer(peerId)?.IsBlocked == true) return;
        var conv = p.GroupId is { } gid ? FindGroup(gid) : FindConversation(peerId);
        if (conv == null || (conv.IsGroup && conv.Group?.IsMember(peerId) != true)) return;
        var msg = FindMessage(conv, p.MessageId);
        if (msg == null || msg.IsDeleted) return;
        SetReaction(conv, msg, peerId, p.Emoji);
    }

    private void HandleTyping(string peerId, TypingPacket p)
    {
        if (p.GroupId == null && FindPeer(peerId)?.IsBlocked == true) return;
        var conv = p.GroupId is { } gid ? FindGroup(gid) : FindConversation(peerId);
        if (conv == null) return;
        if (conv.IsGroup && conv.Group?.IsMember(peerId) != true) return;
        var name = FindPeer(peerId)?.Name ?? "Biri";
        conv.TypingText = conv.IsGroup ? $"{name} yazıyor…" : "yazıyor…";
        _typingUntil[conv.Id] = DateTime.UtcNow.AddSeconds(4.5);
    }

    private void HandleDelete(string peerId, DeletePacket p)
    {
        var conv = p.GroupId is { } gid ? FindGroup(gid) : FindConversation(peerId);
        var msg = FindMessage(conv, p.MessageId);
        if (conv == null || msg == null || msg.IsDeleted) return;
        var allowed = msg.SenderId == peerId || (conv.IsGroup && conv.Group?.IsAdmin(peerId) == true);
        if (!allowed) return;
        MarkDeleted(conv, msg);
    }

    // ------------------------------------------------------------------ Gelen dosyalar

    private sealed class IncomingFile
    {
        public Guid MessageId;
        public string PeerId = "";
        public string ConversationId = "";
        public string FileName = "";
        public string TempPath = "";
        public long Size;
        public long Received;
        public FileStream? Stream;
        public DateTime LastReport;
        public readonly object Gate = new();

        public void Abort()
        {
            lock (Gate)
            {
                Stream?.Dispose();
                Stream = null;
            }
            try { File.Delete(TempPath); } catch { }
        }
    }

    private void RegisterIncomingFile(string peerId, ChatPacket p)
    {
        if (p.FileSize < 0 || p.FileSize > MaxIncomingFile) return;
        if (p.GroupId == null && _blockedIds.ContainsKey(peerId)) return;
        try
        {
            var folder = AppPaths.FilesDir;
            Directory.CreateDirectory(folder);
            var file = new IncomingFile
            {
                MessageId = p.MessageId,
                PeerId = peerId,
                ConversationId = p.GroupId is { } gid ? Conversation.GroupConversationId(gid) : peerId,
                FileName = SanitizeFileName(p.FileName),
                Size = p.FileSize,
                TempPath = Path.Combine(folder, $".netchat-{p.MessageId:N}.part"),
            };
            file.Stream = new FileStream(file.TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            file.Stream.SetLength(p.FileSize);
            if (_incomingFiles.TryRemove(p.MessageId, out var old)) old.Abort();
            _incomingFiles[p.MessageId] = file;
        }
        catch (Exception ex)
        {
            Log.Error("Gelen dosya için yer ayrılamadı", ex);
        }
    }

    private void OnChunkNetworkThread(SecureChannel ch, Guid id, long offset, ReadOnlyMemory<byte> data)
    {
        if (!_incomingFiles.TryGetValue(id, out var file) || file.PeerId != ch.PeerId) return;
        if (offset < 0 || offset + data.Length > file.Size) return;
        lock (file.Gate)
        {
            if (file.Stream == null) return;
            file.Stream.Position = offset;
            file.Stream.Write(data.Span);
            file.Received += data.Length;
        }
        if (DateTime.UtcNow - file.LastReport > TimeSpan.FromMilliseconds(120))
        {
            file.LastReport = DateTime.UtcNow;
            var progress = (double)file.Received / Math.Max(1, file.Size);
            Post(() =>
            {
                var msg = FindMessage(file.ConversationId, id);
                if (msg != null && msg.Transfer != TransferState.Completed)
                {
                    msg.Transfer = TransferState.Transferring;
                    msg.Progress = progress;
                }
            });
        }
    }

    private void FinishIncomingFile(string peerId, FileDonePacket done)
    {
        if (!_incomingFiles.TryGetValue(done.MessageId, out var file) || file.PeerId != peerId) return;
        _incomingFiles.TryRemove(done.MessageId, out _);

        string? finalPath = null;
        try
        {
            string hash;
            lock (file.Gate)
            {
                if (file.Stream == null) return;
                file.Stream.Flush();
                file.Stream.Position = 0;
                hash = Convert.ToHexString(SHA256.HashData(file.Stream));
                file.Stream.Dispose();
                file.Stream = null;
            }
            if (!string.Equals(hash, done.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Dosya bütünlük doğrulaması başarısız");

            // Her dosya kendi klasöründe: aynı adlı dosyalar çakışmaz, "Kaydet..." özgün adı önerir.
            var folder = Path.Combine(AppPaths.FilesDir, file.MessageId.ToString("N"));
            Directory.CreateDirectory(folder);
            finalPath = Path.Combine(folder, file.FileName);
            File.Move(file.TempPath, finalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Dosya alınamadı", ex);
            file.Abort();
            finalPath = null;
        }

        var path = finalPath;
        Post(() =>
        {
            var conv = FindConversation(file.ConversationId);
            var msg = FindMessage(conv, file.MessageId);
            if (msg == null)
            {
                if (path != null) Log.Info($"Sahipsiz dosya kaydedildi: {path}");
                return;
            }
            if (msg.Transfer == TransferState.Completed && path != null)
            {
                // Zaten alınmıştı; kopyayı sil.
                try { File.Delete(path); } catch { }
                return;
            }
            msg.LocalPath = path;
            msg.Transfer = path != null ? TransferState.Completed : TransferState.Failed;
            msg.Progress = path != null ? 1 : 0;
            MarkDirty();
        });
    }

    private static string SanitizeFileName(string? name)
    {
        name = Path.GetFileName(name ?? "");
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimStart('.');
        if (name.Length == 0) name = "dosya";
        if (name.Length > 150) name = name[..100] + Path.GetExtension(name);
        return name;
    }

    // ------------------------------------------------------------------ Kapatma

    public void Dispose()
    {
        _tickTimer.Stop();
        _saveTimer.Stop();
        _settings.PropertyChanged -= OnSettingsChanged;
        SaveNow();
        _discovery.Dispose();
        _connections.Dispose();
        foreach (var file in _incomingFiles.Values) file.Abort();
    }
}

public enum PreflightResult
{
    Ok,
    /// <summary>Aynı profil ağda başka bir oturumda açık.</summary>
    DuplicateSession,
    /// <summary>Ağda aynı adı kullanan başka biri var.</summary>
    NameTaken,
}
