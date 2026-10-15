using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NetChat.Core;
using NetChat.Models;
using NetChat.Services;

namespace NetChat.Views;

public partial class MemberItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsMe { get; init; }
    public bool IsOwner { get; init; }
    public bool IsAdmin { get; init; }
    public bool IsMuted { get; init; }
    public Peer? Peer { get; init; }
    public bool HasActions { get; init; }

    public string DisplayName => IsMe ? $"{Name} (Siz)" : Name;
    public string Initials => Conversation.MakeInitials(Name);
    public System.Windows.Media.ImageSource? AvatarImage => IsMe ? NetChat.Core.ProfilePicture.Decode(App.Settings.Avatar) : Peer?.AvatarImage;

    public string? RoleText => IsOwner ? "Kurucu" : IsAdmin ? "Yönetici" : IsMuted ? "Susturuldu" : null;
}

/// <summary>Açık sohbetin başlık, izin ve üye bilgileri.</summary>
public partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly ChatService _chat = App.Chat;
    private Peer? _peer;

    public Conversation Conversation { get; }

    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isTyping;
    [ObservableProperty] private string? _blockedReason;
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private bool _isOwner;
    [ObservableProperty] private bool _canEditInfo;
    [ObservableProperty] private bool _isMember;
    [ObservableProperty] private bool _onlyAdminsCanSend;
    [ObservableProperty] private bool _onlyAdminsCanEdit;
    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private string _groupDescription = "";
    [ObservableProperty] private string _memberCountText = "";

    public ObservableCollection<MemberItem> Members { get; } = [];

    public bool IsGroup => Conversation.IsGroup;
    public Peer? Peer => _peer;
    public string PeerFingerprint => _peer != null && !string.IsNullOrEmpty(_peer.PublicKey)
        ? Identity.Fingerprint(Convert.FromBase64String(_peer.PublicKey)) : "Bilinmiyor";
    public string MyFingerprint => Identity.Fingerprint(_chat.MyPublicKey);

    public ChatViewModel(Conversation conversation)
    {
        Conversation = conversation;
        Conversation.PropertyChanged += OnConversationChanged;
        AttachPeer(conversation.Peer);
        foreach (var p in _chat.Peers) p.PropertyChanged += OnAnyPeerChanged;
        _chat.Peers.CollectionChanged += OnPeersCollectionChanged;
        Refresh();
    }

    private void AttachPeer(Peer? peer)
    {
        if (_peer != null) _peer.PropertyChanged -= OnPeerChanged;
        _peer = peer;
        if (_peer != null) _peer.PropertyChanged += OnPeerChanged;
        OnPropertyChanged(nameof(Peer));
        OnPropertyChanged(nameof(PeerFingerprint));
    }

    private void OnPeersCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (Peer p in e.NewItems) p.PropertyChanged += OnAnyPeerChanged;
    }

    private void OnConversationChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Conversation.Peer):
                AttachPeer(Conversation.Peer);
                Refresh();
                break;
            case nameof(Conversation.TypingText):
                RefreshSubtitle();
                break;
            case nameof(Conversation.Group):
                Refresh();
                break;
        }
    }

    private void OnPeerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Models.Peer.PresenceText) or nameof(Models.Peer.IsBlocked) or nameof(Models.Peer.PublicKey))
        {
            Refresh();
            OnPropertyChanged(nameof(PeerFingerprint));
        }
    }

    private void OnAnyPeerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsGroup && e.PropertyName == nameof(Models.Peer.Presence)) RefreshSubtitle();
    }

    public void Refresh()
    {
        BlockedReason = _chat.SendBlockedReason(Conversation);
        var g = Conversation.Group;
        if (g != null)
        {
            var me = _chat.MyId;
            IsAdmin = g.IsAdmin(me) && g.IsMember(me) && !g.Disbanded;
            IsOwner = g.OwnerId == me && !g.Disbanded;
            IsMember = g.IsMember(me) && !g.Disbanded;
            CanEditInfo = _chat.CanEditGroupInfo(Conversation);
            OnlyAdminsCanSend = g.OnlyAdminsCanSend;
            OnlyAdminsCanEdit = g.OnlyAdminsCanEdit;
            GroupName = g.Name;
            GroupDescription = g.Description;
            MemberCountText = $"{g.Members.Count} üye";
            RebuildMembers(g);
        }
        RefreshSubtitle();
    }

    private void RebuildMembers(GroupState g)
    {
        var me = _chat.MyId;
        var items = g.Members
            .Select(m =>
            {
                var isOwner = m.Id == g.OwnerId;
                var isAdmin = g.Admins.Contains(m.Id);
                var isMe = m.Id == me;
                var hasActions = !isMe && !g.Disbanded && (
                    (IsAdmin && !isOwner && (!isAdmin || IsOwner)) // çıkar / sustur / yönetici yap
                    || _chat.FindPeer(m.Id) != null);              // mesaj gönder
                return new MemberItem
                {
                    Id = m.Id,
                    Name = isMe ? _chat.MyName : _chat.FindPeer(m.Id)?.Name ?? m.Name,
                    IsMe = isMe,
                    IsOwner = isOwner,
                    IsAdmin = isAdmin || isOwner,
                    IsMuted = g.Muted.Contains(m.Id),
                    Peer = _chat.FindPeer(m.Id),
                    HasActions = hasActions,
                };
            })
            .OrderByDescending(m => m.IsMe)
            .ThenByDescending(m => m.IsOwner)
            .ThenByDescending(m => m.IsAdmin)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);

        Members.Clear();
        foreach (var item in items) Members.Add(item);
    }

    private void RefreshSubtitle()
    {
        IsTyping = Conversation.TypingText != null;
        if (IsTyping)
        {
            Subtitle = Conversation.TypingText!;
            return;
        }

        if (Conversation.IsGroup)
        {
            var g = Conversation.Group;
            if (g == null)
            {
                Subtitle = "";
                return;
            }
            if (g.Disbanded)
            {
                Subtitle = "Grup dağıtıldı";
                return;
            }
            var online = g.Members.Count(m => m.Id != _chat.MyId && _chat.FindPeer(m.Id)?.Presence == PresenceState.Online);
            var names = g.Members
                .Select(m => m.Id == _chat.MyId ? "Siz" : _chat.FindPeer(m.Id)?.Name ?? m.Name)
                .OrderBy(n => n == "Siz" ? 1 : 0)
                .Take(6);
            var list = string.Join(", ", names) + (g.Members.Count > 6 ? "…" : "");
            Subtitle = online > 0 ? $"{online} çevrimiçi · {list}" : list;
            return;
        }

        Subtitle = _peer == null ? "" : _peer.IsBlocked ? "Engellendi" : _peer.PresenceText;
    }

    public void Dispose()
    {
        Conversation.PropertyChanged -= OnConversationChanged;
        if (_peer != null) _peer.PropertyChanged -= OnPeerChanged;
        foreach (var p in _chat.Peers) p.PropertyChanged -= OnAnyPeerChanged;
        _chat.Peers.CollectionChanged -= OnPeersCollectionChanged;
    }
}
