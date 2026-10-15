using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NetChat.Core;

/// <summary>Beni gruplara kimler ekleyebilir.</summary>
public enum GroupAddMode
{
    Everyone,
    Nobody,
    /// <summary>Yalnızca <see cref="AppSettings.GroupAddOnly"/> listesindekiler.</summary>
    Only,
    /// <summary><see cref="AppSettings.GroupAddExcept"/> listesindekiler hariç herkes.</summary>
    Except,
}

public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Uygulama ayarları. Tek bir örnek vardır (App.Settings). Tüm ayarlar şifreli profil paketinde saklanır;
/// cihazda ayar dosyası tutulmaz. Giriş ekranında varsayılanlar geçerlidir, girişte paketten doldurulur.
/// </summary>
public partial class AppSettings : ObservableObject
{
    [ObservableProperty] private string _displayName = "";
    /// <summary>Profil resmi (Base64 JPEG, <see cref="ProfilePicture"/>); yoksa null.</summary>
    [ObservableProperty] private string? _avatar;
    [ObservableProperty] private bool _notificationsEnabled = true;
    [ObservableProperty] private bool _showMessagePreview = true;
    [ObservableProperty] private bool _notificationSound = true;
    [ObservableProperty] private bool _sendReadReceipts = true;
    /// <summary>
    /// Mesaj geçmişi profil dosyasında saklansın mı. Kapalıyken yalnızca okunmamış gelen mesajlar ve henüz
    /// iletilemeyen giden mesajlar saklanır; okunan/iletilen mesajlar oturum bitince kaybolur.
    /// Yeni profillerde kapalı; bu ayar eklenmeden önce oluşturulmuş profillerde açık (geçmişleri silinmesin).
    /// </summary>
    [ObservableProperty] private bool _keepMessageHistory;
    [ObservableProperty] private bool _shareOnlineStatus = true;
    [ObservableProperty] private bool _shareLastActive = true;
    /// <summary>Bildirim alanındaki zil: tüm bildirimleri geçici olarak sessize alır.</summary>
    [ObservableProperty] private bool _notificationsMuted;
    /// <summary>Kullanıcının uygulamada en son aktif olduğu an.</summary>
    [ObservableProperty] private DateTimeOffset? _lastActive;
    [ObservableProperty] private bool _runInBackground = true;
    [ObservableProperty] private bool _backgroundHintShown;
    [ObservableProperty] private List<string> _recentEmoji = [];
    [ObservableProperty] private GroupAddMode _groupAddMode = GroupAddMode.Everyone;
    [ObservableProperty] private List<string> _groupAddOnly = [];
    [ObservableProperty] private List<string> _groupAddExcept = [];
    [ObservableProperty] private AppThemeMode _theme = AppThemeMode.System;
    [ObservableProperty] private bool _animationsEnabled = true;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private double _windowWidth = 1180;
    [ObservableProperty] private double _windowHeight = 760;
    [ObservableProperty] private double _listWidth = 330;
    [ObservableProperty] private double _infoWidth = 320;

    /// <summary>Profil paketine yazılması gereken bir ayar değişti.</summary>
    public event Action? ProfileChanged;

    private int _suspend;

    private static IEnumerable<PropertyInfo> SettableProperties =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() == null);

    public static AppSettings Create()
    {
        var settings = new AppSettings();
        settings.PropertyChanged += (_, e) =>
        {
            if (settings._suspend > 0) return;
            settings.ProfileChanged?.Invoke();
        };
        return settings;
    }

    public byte[] SerializeForProfile() =>
        JsonSerializer.SerializeToUtf8Bytes(SettableProperties.ToDictionary(p => p.Name, p => p.GetValue(this)));

    /// <summary>Profil paketindeki ayarları uygular.</summary>
    public void ApplyProfile(byte[]? json)
    {
        AppSettings? source = null;
        if (json is { Length: > 0 })
        {
            try
            {
                source = JsonSerializer.Deserialize<AppSettings>(json);
            }
            catch (Exception ex)
            {
                Log.Error("Profil ayarları okunamadı", ex);
            }
        }
        CopyFrom(source ?? new AppSettings());
        // Ayar profilde hiç yoksa (önceki sürümden kalan profil) geçmiş açık kabul edilir.
        if (source != null && !HasProperty(json!, nameof(KeepMessageHistory))) KeepMessageHistory = true;
    }

    private static bool HasProperty(byte[] json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Çıkışta tüm ayarları varsayılana döndürür (giriş ekranında profile ait hiçbir şey kalmaz).</summary>
    public void ResetProfileFields() => CopyFrom(new AppSettings());

    private void CopyFrom(AppSettings source)
    {
        _suspend++;
        try
        {
            foreach (var p in SettableProperties)
                p.SetValue(this, p.GetValue(source));
        }
        finally
        {
            _suspend--;
        }
    }
}
