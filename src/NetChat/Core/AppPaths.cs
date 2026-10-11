using System.IO;

namespace NetChat.Core;

/// <summary>
/// Uygulamanın veri klasörleri. "--profile X" argümanı ile aynı makinede birden fazla
/// bağımsız örnek çalıştırılabilir (test için).
/// </summary>
public static class AppPaths
{
    public const string AppName = "NetChat 26";
    public const string AppId = "NetChat26";

    public static string Profile { get; private set; } = "default";

    /// <summary>
    /// %AppData%\NetChat. Burada yalnızca varsayılan konumdaki şifreli profil paketleri bulunur;
    /// mesajlar, gruplar ve ayarlar dahil tüm kullanıcı verisi paketin içindedir.
    /// </summary>
    public static string DataDir { get; private set; } = "";

    /// <summary>Varsayılan konumdaki (AppData) şifreli profil paketleri.</summary>
    public static string ProfilesDir => Path.Combine(DataDir, "UserProfiles");

    /// <summary>
    /// Windows'un doğrudan dosyadan okuması gereken kaynaklar (ör. .nprofile simgesi). Uygulamaya gömülüdür,
    /// açılışta buraya çıkarılır. Kişisel veri içermez; tüm örnekler (--profile) için ortaktır.
    /// </summary>
    public static string ResourceDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetChat", "res");

    /// <summary>Bildirim görselleri için geçici klasör; oturum kapanınca silinir.</summary>
    public static string TempDir => Path.Combine(Path.GetTempPath(), $"{AppId}-{Profile}");

    /// <summary>Ayrıntılı (bilgi) günlüğü yalnızca "--log" ile açılır; hatalar her zaman yazılır (bkz. <see cref="Log"/>).</summary>
    public static bool LoggingEnabled { get; set; }

    public static string DefaultDownloadDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NetChat");

    public static void Initialize(string? profile)
    {
        if (!string.IsNullOrWhiteSpace(profile))
            Profile = string.Concat(profile.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (string.IsNullOrEmpty(Profile)) Profile = "default";

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetChat");
        DataDir = Profile == "default" ? root : Path.Combine(root, "profiles", Profile);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ProfilesDir);
    }
}

/// <summary>
/// Günlük: %AppData%\NetChat\logging\{tarih}[-örnek].log
///  - Hatalar her zaman yazılır (yığın izi dahil). Kullanıcı klasörü yolu "%USERPROFILE%" ile maskelenir.
///  - Bilgi kayıtları (kişi adları içerebilir) yalnızca "--log" ile yazılır.
///  - Dosyalar 14 günden eskiyse silinir; tek dosya 5 MB'ı geçerse yeni kayıtlar yazılmaz.
/// </summary>
public static class Log
{
    private const long MaxFileSize = 5 * 1024 * 1024;
    private static readonly object Gate = new();
    private static bool _cleaned;

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetChat", "logging");

    private static string CurrentFile =>
        Path.Combine(Directory, $"{DateTime.Now:yyyy-MM-dd}{(AppPaths.Profile == "default" ? "" : "-" + AppPaths.Profile)}.log");

    public static void Info(string message)
    {
        if (AppPaths.LoggingEnabled) Write("INFO", message);
        else System.Diagnostics.Debug.WriteLine($"[INFO] {message}");
    }

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}");

    private static string Sanitize(string text)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? text : text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    private static void Write(string level, string message)
    {
        message = Sanitize(message);
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (!_cleaned)
                {
                    _cleaned = true;
                    foreach (var old in new DirectoryInfo(Directory).EnumerateFiles("*.log"))
                        if (old.LastWriteTime < DateTime.Now.AddDays(-14)) old.Delete();
                }
                var file = CurrentFile;
                if (File.Exists(file) && new FileInfo(file).Length > MaxFileSize) return;
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Günlük yazılamazsa uygulamayı etkilememeli.
        }
        System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
    }
}
