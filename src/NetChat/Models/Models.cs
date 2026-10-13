using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NetChat.Models;

public enum MessageKind
{
    Text,
    File,
    System,
}

public enum MessageStatus
{
    /// <summary>Alıcı çevrimdışı; bağlandığında gönderilecek.</summary>
    Pending,
    Sent,
    Delivered,
    Read,
    Failed,
}

public enum TransferState
{
    None,
    Waiting,
    Transferring,
    Completed,
    Failed,
}

/// <summary>Ağda görülen (veya daha önce görülmüş) bir NetChat kullanıcısı.</summary>
public partial class Peer : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _publicKey = "";
    [ObservableProperty] private string? _address;
    [ObservableProperty] private int _port;
    /// <summary>Ağda en son ne zaman duyulduğu (bağlantı zaman aşımı için; arayüzde gösterilmez).</summary>
    [ObservableProperty] private DateTimeOffset _lastSeen;

    /// <summary>Kişinin bildirdiği son aktiflik zamanı; paylaşmıyorsa null.</summary>
    [ObservableProperty] private DateTimeOffset? _lastActive;

    /// <summary>Kişi çevrimiçi/boşta/çevrimdışı durumunu paylaşıyor mu.</summary>
    [ObservableProperty] private bool _sharesStatus = true;

    [ObservableProperty] private bool _isBlocked;

    /// <summary>Ağda erişilebilir mi (mesaj teslimi için). Durum gösterimi için <see cref="Presence"/> kullanılır.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isOnline;

    /// <summary>Kişinin bildirdiği durum: true = pencere önde (çevrimiçi), false = boşta.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isActive;

    [JsonIgnore]
    public IPAddress? IpAddress => IPAddress.TryParse(Address, out var ip) ? ip : null;

    [JsonIgnore]
    public string Initials => Conversation.MakeInitials(Name);

    /// <summary>Bu kişinin gizlilik ayarı bizim onu gruplara eklememize izin veriyor mu (son duyurusuna göre).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(CanBeAddedToGroups))]
    private bool _allowsGroupAdd = true;

    /// <summary>Gruplara eklenebilir: izin veriyor ve bizi engellememiş.</summary>
    [JsonIgnore]
    public bool CanBeAddedToGroups => AllowsGroupAdd && !BlocksMe;

    /// <summary>
    /// Karşılıklılık: kendi çevrimiçi durumunu / son aktifliğini gizleyen kullanıcı başkalarınınkini de göremez.
    /// ChatService, kullanıcının ayarlarına göre doldurur.
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _viewerSharesStatus = true;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _viewerSharesLastActive = true;

    /// <summary>Bu oturumda ağda görüldü mü. Görülmediyse son aktiflik yalnızca tarih olarak gösterilir.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _seenThisSession;

    /// <summary>Bu kişi bizi engellemiş: durum bilgisini bizimle paylaşmıyor.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _blocksMe;

    [JsonIgnore]
    public PresenceState Presence =>
        !SharesStatus || !ViewerSharesStatus || IsBlocked || BlocksMe ? PresenceState.Hidden
        : !IsOnline ? PresenceState.Offline
        : IsActive ? PresenceState.Online
        : PresenceState.Idle;

    [JsonIgnore] public bool ShowOnlineDot => Presence == PresenceState.Online;

    /// <summary>
    /// "Ağdaki kişiler" listesindeki bölümün sırası: 0 Çevrimiçi, 1 Boşta, 2 Çevrimdışı. Durumunu gizleyen ama ağda
    /// olan kişi "Çevrimiçi" bölümünde görünür (ağda olduğu zaten listede görünmesinden bellidir).
    /// </summary>
    [JsonIgnore] public int PresenceGroupOrder => !IsOnline ? 2 : Presence == PresenceState.Idle ? 1 : 0;

    [JsonIgnore] public string PresenceGroup => PresenceGroupOrder switch { 0 => "Çevrimiçi", 1 => "Boşta", _ => "Çevrimdışı" };
    [JsonIgnore] public bool ShowIdleDot => Presence == PresenceState.Idle;

    /// <summary>"Çevrimiçi" veya "Son aktiflik: 00.04 · Boşta|Çevrimdışı".</summary>
    [JsonIgnore]
    public string PresenceText
    {
        get
        {
            if (IsBlocked) return "Engellendi";
            if (Presence == PresenceState.Online) return "Çevrimiçi";
            var parts = new List<string>(2);
            if (LastActive is { } la && ViewerSharesLastActive && !IsBlocked && !BlocksMe)
                parts.Add("Son aktiflik: " + (SeenThisSession ? FormatLastActive(la) : FormatDate(la)));
            if (Presence == PresenceState.Idle) parts.Add("Boşta");
            else if (Presence == PresenceState.Offline) parts.Add("Çevrimdışı");
            return string.Join(" · ", parts);
        }
    }

    public static string FormatLastActive(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        if (local.Date == DateTime.Today) return local.ToString("HH.mm");
        if (local.Date == DateTime.Today.AddDays(-1)) return "dün " + local.ToString("HH.mm");
        return local.ToString("dd.MM.yyyy HH.mm");
    }

    /// <summary>Bu oturumda ağda görülmeyen kişiler için: "11 Ekim 2026".</summary>
    public static string FormatDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));

    private void RaisePresence()
    {
        OnPropertyChanged(nameof(Presence));
        OnPropertyChanged(nameof(ShowOnlineDot));
        OnPropertyChanged(nameof(ShowIdleDot));
        OnPropertyChanged(nameof(PresenceText));
        OnPropertyChanged(nameof(PresenceGroupOrder));
        OnPropertyChanged(nameof(PresenceGroup));
    }

    /// <summary>Gün değiştiğinde "dün" gibi göreli metinlerin yenilenmesi için.</summary>
    public void RefreshPresenceText() => OnPropertyChanged(nameof(PresenceText));

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Initials));
    partial void OnIsOnlineChanged(bool value) => RaisePresence();
    partial void OnIsActiveChanged(bool value) => RaisePresence();
    partial void OnSharesStatusChanged(bool value) => RaisePresence();
    partial void OnLastActiveChanged(DateTimeOffset? value) => RaisePresence();
    partial void OnIsBlockedChanged(bool value) => RaisePresence();
    partial void OnViewerSharesStatusChanged(bool value) => RaisePresence();
    partial void OnViewerSharesLastActiveChanged(bool value) => RaisePresence();
    partial void OnSeenThisSessionChanged(bool value) => RaisePresence();
    partial void OnBlocksMeChanged(bool value)
    {
        RaisePresence();
        OnPropertyChanged(nameof(CanBeAddedToGroups));
    }

    public override string ToString() => Name;
}

public enum PresenceState
{
    Hidden,
    Online,
    Idle,
    Offline,
}

/// <summary>Mesaja bırakılan bir ifadenin özeti (balonun altındaki çip).</summary>
public sealed record ReactionChip(string Emoji, int Count, bool Mine, string Names);

public class GroupMember
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Grubun ağ üzerinden paylaşılan, imzalı durumu (kurallar: <see cref="NetChat.Services.GroupRules"/>).</summary>
public class GroupState
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public List<string> Admins { get; set; } = [];
    public List<GroupMember> Members { get; set; } = [];
    public List<string> Muted { get; set; } = [];
    public List<string> Removed { get; set; } = [];
    public bool OnlyAdminsCanSend { get; set; }
    public bool OnlyAdminsCanEdit { get; set; }
    public bool Disbanded { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Bu sürümü oluşturan değişiklik (imzaya dahil; aktarılan sürümlerde sistem mesajı için).</summary>
    public GroupEvent? LastEvent { get; set; }
    /// <summary>Bu sürümü imzalayan kullanıcı ve ECDSA imzası (base64). Önceki sürümlerden kalan durumlarda boş.</summary>
    public string? SignerId { get; set; }
    public string? Signature { get; set; }

    private static readonly System.Text.Json.JsonSerializerOptions CanonicalJson = new();

    /// <summary>İmzalanan içerik: imza alanı hariç her şey.</summary>
    public byte[] SigningBytes()
    {
        var c = Clone();
        c.Signature = null;
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(c, CanonicalJson);
    }

    /// <summary>Yalnızca grup içeriği (sürüm, imza ve olay hariç): iki durumun aynı değişikliği temsil edip etmediğini karşılaştırmak için.</summary>
    public string ContentKey()
    {
        var c = Clone();
        c.Version = 0;
        c.LastEvent = null;
        c.SignerId = null;
        c.Signature = null;
        return System.Text.Json.JsonSerializer.Serialize(c, CanonicalJson);
    }

    public bool IsMember(string id) => Members.Any(m => m.Id == id);
    public bool IsAdmin(string id) => id == OwnerId || Admins.Contains(id);
    public bool IsMuted(string id) => Muted.Contains(id);

    public bool CanSend(string id) =>
        !Disbanded && IsMember(id) && !IsMuted(id) && (!OnlyAdminsCanSend || IsAdmin(id));

    public string MemberName(string id) => Members.FirstOrDefault(m => m.Id == id)?.Name ?? "Bilinmeyen";

    public GroupState Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        OwnerId = OwnerId,
        Admins = [.. Admins],
        Members = Members.Select(m => new GroupMember { Id = m.Id, Name = m.Name }).ToList(),
        Muted = [.. Muted],
        Removed = [.. Removed],
        OnlyAdminsCanSend = OnlyAdminsCanSend,
        OnlyAdminsCanEdit = OnlyAdminsCanEdit,
        Disbanded = Disbanded,
        Version = Version,
        CreatedAt = CreatedAt,
        LastEvent = LastEvent,
        SignerId = SignerId,
        Signature = Signature,
    };
}

public enum GroupEventKind
{
    Created,
    Added,
    Removed,
    Promoted,
    Demoted,
    Muted,
    Unmuted,
    Renamed,
    DescriptionChanged,
    SendPolicyChanged,
    EditPolicyChanged,
    Disbanded,
    Left,
    /// <summary>Eklenen kişi gizlilik ayarı nedeniyle katılmadı.</summary>
    Declined,
}

public class GroupEvent
{
    public GroupEventKind Kind { get; set; }
    public string ActorId { get; set; } = "";
    public string ActorName { get; set; } = "";
    public List<GroupMember> Targets { get; set; } = [];
    public string? Value { get; set; }
    public bool Flag { get; set; }

    /// <summary>Olayı okuyan kullanıcının bakış açısından Türkçe metne çevirir.</summary>
    public string Describe(string myId)
    {
        var meActor = ActorId == myId;
        var actor = meActor ? "Siz" : ActorName;
        var targetsMe = Targets.Any(t => t.Id == myId);
        var names = string.Join(", ", Targets.Select(t => t.Id == myId ? "siz" : t.Name));

        return Kind switch
        {
            GroupEventKind.Created => meActor ? "Grubu oluşturdunuz" : $"{actor} grubu oluşturdu ve sizi ekledi",
            GroupEventKind.Added => meActor ? $"Gruba eklediniz: {names}"
                : targetsMe ? $"{actor} sizi gruba ekledi" : $"{actor} gruba ekledi: {names}",
            GroupEventKind.Removed => meActor ? $"Gruptan çıkardınız: {names}"
                : targetsMe ? $"{actor} sizi gruptan çıkardı" : $"{actor} gruptan çıkardı: {names}",
            GroupEventKind.Promoted => meActor ? $"Yönetici yaptınız: {names}"
                : targetsMe ? $"{actor} sizi yönetici yaptı" : $"{actor} yönetici yaptı: {names}",
            GroupEventKind.Demoted => meActor ? $"Yöneticilikten aldınız: {names}"
                : targetsMe ? $"{actor} sizi yöneticilikten aldı" : $"{actor} yöneticilikten aldı: {names}",
            GroupEventKind.Muted => meActor ? $"Susturdunuz: {names}"
                : targetsMe ? $"{actor} sizi susturdu" : $"{actor} susturdu: {names}",
            GroupEventKind.Unmuted => meActor ? $"Susturmayı kaldırdınız: {names}"
                : targetsMe ? $"{actor} susturmanızı kaldırdı" : $"{actor} susturmayı kaldırdı: {names}",
            GroupEventKind.Renamed => meActor ? $"Grup adını \"{Value}\" olarak değiştirdiniz"
                : $"{actor} grup adını \"{Value}\" olarak değiştirdi",
            GroupEventKind.DescriptionChanged => meActor ? "Grup açıklamasını değiştirdiniz" : $"{actor} grup açıklamasını değiştirdi",
            GroupEventKind.SendPolicyChanged => Flag
                ? (meActor ? "Artık yalnızca yöneticiler mesaj gönderebilir" : $"{actor}: artık yalnızca yöneticiler mesaj gönderebilir")
                : (meActor ? "Artık tüm üyeler mesaj gönderebilir" : $"{actor}: artık tüm üyeler mesaj gönderebilir"),
            GroupEventKind.EditPolicyChanged => Flag
                ? (meActor ? "Grup bilgilerini artık yalnızca yöneticiler düzenleyebilir" : $"{actor}: grup bilgilerini artık yalnızca yöneticiler düzenleyebilir")
                : (meActor ? "Grup bilgilerini artık tüm üyeler düzenleyebilir" : $"{actor}: grup bilgilerini artık tüm üyeler düzenleyebilir"),
            GroupEventKind.Disbanded => meActor ? "Grubu dağıttınız" : $"{actor} grubu dağıttı",
            GroupEventKind.Left => meActor ? "Gruptan ayrıldınız" : $"{actor} gruptan ayrıldı",
            GroupEventKind.Declined => meActor ? "Gizlilik ayarlarınız nedeniyle gruba katılmadınız" : $"{actor} gizlilik ayarları nedeniyle gruba eklenemedi",
            _ => "",
        };
    }
}

public partial class ChatMessage : ObservableObject
{
    public Guid Id { get; set; }
    public string ConversationId { get; set; } = "";
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public MessageKind Kind { get; set; }
    public bool IsOutgoing { get; set; }

    [ObservableProperty] private string? _text;
    [ObservableProperty] private MessageStatus _status;
    [ObservableProperty] private bool _isDeleted;

    // Dosya mesajları
    [ObservableProperty] private string? _fileName;
    [ObservableProperty] private long _fileSize;
    [ObservableProperty] private string? _localPath;
    [ObservableProperty] private TransferState _transfer;

    [ObservableProperty]
    [property: JsonIgnore]
    private double _progress;

    /// <summary>Henüz ulaştırılamamış alıcılar (çevrimdışı olanlar). Bağlandıklarında yeniden gönderilir.</summary>
    public HashSet<string> PendingRecipients { get; set; } = [];
    public HashSet<string> DeliveredTo { get; set; } = [];
    public HashSet<string> ReadBy { get; set; } = [];

    /// <summary>Gelen mesaj okundu mu (yerel).</summary>
    public bool IsRead { get; set; }

    // Yanıt: alıntılanan mesajın özeti mesajla birlikte taşınır (orijinal silinmiş/yüklenmemiş olsa da görünsün).
    public Guid? ReplyToId { get; set; }
    public string? ReplySenderId { get; set; }
    public string? ReplySenderName { get; set; }
    public string? ReplyText { get; set; }

    /// <summary>Alıntıda görünen ad (alıntılanan mesaj bizimse "Siz").</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string? _replyDisplayName;

    /// <summary>Grup mesajında etiketlenen üyeler.</summary>
    public List<GroupMember> Mentions { get; set; } = [];

    /// <summary>Kullanıcı Id → bıraktığı ifade.</summary>
    public Dictionary<string, string> Reactions { get; set; } = [];

    [JsonIgnore] public bool HasReply => ReplyToId != null && !IsDeleted;
    [JsonIgnore] public bool HasReactions => Reactions.Count > 0 && !IsDeleted;

    [ObservableProperty]
    [property: JsonIgnore]
    private IReadOnlyList<ReactionChip> _reactionChips = [];

    /// <summary>Yanıttan bu mesaja atlandığında kısa süreli vurgu.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isHighlighted;

    /// <summary>İfade çiplerini yeniden hesaplar. <paramref name="nameOf"/> kullanıcı Id → görünen ad.</summary>
    public void RefreshReactions(string myId, Func<string, string> nameOf)
    {
        ReactionChips = Reactions
            .GroupBy(r => r.Value)
            .OrderByDescending(g => g.Count())
            .Select(g => new ReactionChip(g.Key, g.Count(), g.Any(r => r.Key == myId),
                string.Join(", ", g.Select(r => r.Key == myId ? "Siz" : nameOf(r.Key)))))
            .ToList();
        OnPropertyChanged(nameof(HasReactions));
    }

    // --- Yalnızca arayüz için ---
    [ObservableProperty]
    [property: JsonIgnore]
    private string? _dateHeader;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _showSender;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isFirstInRun = true;

    [JsonIgnore] public bool AnimateIn { get; set; }
    [JsonIgnore] public bool IsGroupMessage { get; set; }
    [JsonIgnore] public bool IsSystem => Kind == MessageKind.System;
    [JsonIgnore] public bool IsFile => Kind == MessageKind.File && !IsDeleted;
    [JsonIgnore] public bool IsText => Kind == MessageKind.Text || IsDeleted;

    [JsonIgnore]
    public bool IsImage => IsFile && Transfer == TransferState.Completed && LocalPath != null &&
                           ImageExtensions.Contains(System.IO.Path.GetExtension(LocalPath).ToLowerInvariant());

    [JsonIgnore]
    public string DisplayText => IsDeleted ? (IsOutgoing ? "Bu mesajı sildiniz" : "Bu mesaj silindi") : Text ?? "";

    [JsonIgnore]
    public string FileSizeText => FormatSize(FileSize);

    [JsonIgnore]
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm");

    private static readonly HashSet<string> ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".jfif"];

    partial void OnIsDeletedChanged(bool value)
    {
        OnPropertyChanged(nameof(HasReply));
        OnPropertyChanged(nameof(HasReactions));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(IsFile));
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsImage));
    }

    partial void OnTextChanged(string? value) => OnPropertyChanged(nameof(DisplayText));
    partial void OnTransferChanged(TransferState value) => OnPropertyChanged(nameof(IsImage));
    partial void OnLocalPathChanged(string? value) => OnPropertyChanged(nameof(IsImage));
    partial void OnFileSizeChanged(long value) => OnPropertyChanged(nameof(FileSizeText));

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    public string PreviewText => Kind switch
    {
        _ when IsDeleted => DisplayText,
        MessageKind.File => $"🧾 {FileName} ({FileSizeText})",
        _ => (Text ?? "").ReplaceLineEndings(" "),
    };
}

public partial class Conversation : ObservableObject
{
    /// <summary>Bire bir sohbette karşı tarafın Id'si; grupta "g:{GroupId}".</summary>
    public string Id { get; set; } = "";
    public bool IsGroup { get; set; }
    public string? PeerId { get; set; }
    public ObservableCollection<ChatMessage> Messages { get; set; } = [];
    /// <summary>Grubun son imzalı sürümleri (eskiden yeniye); geride kalan üyelere aktarılır.</summary>
    public List<GroupState> GroupHistory { get; set; } = [];

    [ObservableProperty] private GroupState? _group;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private int _unreadCount;
    [ObservableProperty] private DateTimeOffset _lastActivity;
    [ObservableProperty] private bool _isPinned;

    /// <summary>Sessize alınmış sohbet: bildirim gösterilmez (etiketlenme hariç).</summary>
    [ObservableProperty] private bool _isMuted;

    [ObservableProperty]
    [property: JsonIgnore]
    private Peer? _peer;

    [ObservableProperty]
    [property: JsonIgnore]
    private string? _typingText;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _previewText = "";

    [ObservableProperty]
    [property: JsonIgnore]
    private string _previewPrefix = "";

    [ObservableProperty]
    [property: JsonIgnore]
    private ChatMessage? _lastMessage;

    [JsonIgnore]
    public string Initials => MakeInitials(Title);

    [JsonIgnore]
    public string ColorKey => Id;

    public static string GroupConversationId(Guid groupId) => "g:" + groupId.ToString("N");

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(Initials));

    public override string ToString() => Title;

    public static string MakeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = char.ToUpper(parts[0][0], System.Globalization.CultureInfo.CurrentCulture);
        if (parts.Length == 1) return first.ToString();
        var second = char.ToUpper(parts[^1][0], System.Globalization.CultureInfo.CurrentCulture);
        return $"{first}{second}";
    }

    /// <summary>Tarih ayırıcıları, gönderen adı görünürlüğü ve önizlemeyi yeniden hesaplar.</summary>
    public void RefreshLayout(string myId, int fromIndex = 0)
    {
        for (int i = Math.Max(0, fromIndex); i < Messages.Count; i++)
        {
            var m = Messages[i];
            var prev = i > 0 ? Messages[i - 1] : null;
            var day = m.Timestamp.ToLocalTime().Date;
            m.DateHeader = prev == null || prev.Timestamp.ToLocalTime().Date != day ? FormatDay(day) : null;
            var sameRun = prev != null && m.DateHeader == null && !prev.IsSystem && !m.IsSystem &&
                          prev.SenderId == m.SenderId && (m.Timestamp - prev.Timestamp) < TimeSpan.FromMinutes(5);
            m.IsFirstInRun = !sameRun;
            m.IsGroupMessage = IsGroup;
            m.ShowSender = IsGroup && !m.IsOutgoing && !m.IsSystem && !sameRun;
            if (m.ReplyToId != null) m.ReplyDisplayName = m.ReplySenderId == myId ? "Siz" : m.ReplySenderName;
        }
        RefreshPreview(myId);
    }

    public void RefreshPreview(string myId)
    {
        var last = Messages.LastOrDefault();
        LastMessage = last;
        if (last == null)
        {
            PreviewText = IsGroup ? "Grup oluşturuldu" : "Sohbete başlayın";
            PreviewPrefix = "";
            return;
        }
        PreviewText = last.PreviewText;
        PreviewPrefix = last.IsSystem ? "" : last.IsOutgoing ? "Sen: " : IsGroup ? last.SenderName + ": " : "";
    }

    public static string FormatDay(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return "Bugün";
        if (day == today.AddDays(-1)) return "Dün";
        if ((today - day).TotalDays < 7) return day.ToString("dddd");
        return day.ToString("d MMMM yyyy");
    }
}
