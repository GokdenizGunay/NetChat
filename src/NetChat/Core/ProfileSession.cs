using System.IO;
using System.Security.Cryptography;

namespace NetChat.Core;

/// <summary>
/// Açık profil oturumu. Profil verisi (kimlik, ayarlar, sohbetler) her değişiklikten kısa süre sonra
/// şifreli .nprofile dosyasına yazılır. Oturum boyunca dosya açık ve kilitli tutulur: Windows silme,
/// taşıma veya yeniden adlandırmaya "dosya kullanımda" diyerek izin vermez (okuma/kopyalama serbesttir).
/// Dosyaya erişilemezse (ör. USB bellek çıkarıldı) <see cref="Lost"/> tetiklenir.
/// </summary>
public sealed class ProfileSession : IDisposable
{
    private const string LostMessage = "Profil dosyasına erişilemiyor. Profilin bulunduğu sürücü çıkarılmış veya profil dosyası silinmiş olabilir.";

    private readonly byte[] _key;
    private readonly object _gate = new();
    private readonly Timer _debounce;
    private readonly Timer _watch;
    private FileStream _file;
    private byte[]? _settingsJson;
    private byte[]? _storeJson;
    private bool _dirty;
    private bool _lost;
    private bool _disposed;
    private bool _active;

    public string FilePath { get; private set; }
    public ProfileHeader Header { get; }
    public Identity Identity { get; }
    public ProfilePayload InitialPayload { get; }

    /// <summary>Profil dosyasına erişim kayboldu (arka plan iş parçacığında, bir kez).</summary>
    public event Action<string>? Lost;

    public bool IsInDefaultLocation =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(FilePath)), Path.GetFullPath(AppPaths.ProfilesDir), StringComparison.OrdinalIgnoreCase);

    private ProfileSession(FileStream file, ProfileHeader header, ProfilePayload payload, byte[] key)
    {
        _file = file;
        FilePath = file.Name;
        Header = header;
        InitialPayload = payload;
        Identity = Identity.FromPkcs8(payload.IdentityPkcs8);
        _key = key;
        _settingsJson = payload.SettingsJson;
        _storeJson = payload.StoreJson;
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _watch = new Timer(_ => CheckAccess(), null, 2000, 2000);
    }

    public static string DefaultPathFor(string id) => Path.Combine(AppPaths.ProfilesDir, id + ProfileFile.Extension);

    /// <summary>Dosyayı oturum boyunca kilitli açar: başkaları yalnızca okuyabilir; silme ve yazma engellenir.</summary>
    private static FileStream Lock(string path, FileMode mode)
    {
        try
        {
            return new FileStream(path, mode, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException) && mode == FileMode.Open && File.Exists(path))
        {
            throw new InvalidProfileException("Bu profil bu bilgisayarda zaten açık.");
        }
    }

    private static void WriteAll(FileStream fs, byte[] data)
    {
        fs.Position = 0;
        fs.Write(data);
        fs.SetLength(data.Length);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Yeni profil oluşturur. <paramref name="folder"/> null ise varsayılan konum (AppData) kullanılır.</summary>
    public static ProfileSession Create(string name, string password, string? folder, byte[]? settingsJson = null)
    {
        var identity = Identity.CreateNew();
        var header = ProfileFile.NewHeader(identity.Id, name);
        var key = ProfileFile.DeriveKey(password, Convert.FromBase64String(header.Salt), header.Iterations);
        var payload = new ProfilePayload { IdentityPkcs8 = identity.ExportPkcs8(), SettingsJson = settingsJson };
        var path = folder == null ? DefaultPathFor(identity.Id) : ProfileFile.UniquePath(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fs = Lock(path, FileMode.CreateNew);
        try
        {
            WriteAll(fs, ProfileFile.Encode(header, payload, key));
        }
        catch
        {
            fs.Dispose();
            File.Delete(path);
            throw;
        }
        return new ProfileSession(fs, header, payload, key);
    }

    public static ProfileSession Open(string path, string password)
    {
        var fs = Lock(path, FileMode.Open);
        try
        {
            var data = new byte[fs.Length];
            fs.ReadExactly(data);
            var (header, payload, key) = ProfileFile.Open(data, password);
            return new ProfileSession(fs, header, payload, key);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public void UpdateSettings(byte[] json, string displayName)
    {
        lock (_gate)
        {
            _settingsJson = json;
            if (!string.IsNullOrWhiteSpace(displayName)) Header.Name = displayName;
            _dirty = true;
        }
        Schedule();
    }

    public void UpdateStore(byte[] json)
    {
        lock (_gate)
        {
            _storeJson = json;
            _dirty = true;
        }
        Schedule();
    }

    /// <summary>
    /// Ağ ön kontrolü geçildi: bundan sonra değişiklikler dosyaya yazılır. Öncesinde (ör. aynı profil başka bir
    /// yerde açıkken reddedilen oturumda) dosyaya hiçbir şey yazılmaz; böylece diğer oturumun verisi ezilmez.
    /// </summary>
    public void Activate()
    {
        _active = true;
        Schedule();
    }

    private void Schedule()
    {
        if (!_disposed && _active) _debounce.Change(400, Timeout.Infinite);
    }

    private byte[] Encode() => ProfileFile.Encode(Header,
        new ProfilePayload { IdentityPkcs8 = Identity.ExportPkcs8(), SettingsJson = _settingsJson, StoreJson = _storeJson }, _key);

    /// <summary>Bekleyen değişiklikleri dosyaya yazar. Başarılıysa true.</summary>
    public bool Flush()
    {
        lock (_gate)
        {
            if (!_dirty || !_active) return true;
            if (_lost) return false;
            try
            {
                WriteAll(_file, Encode());
                _dirty = false;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Log.Error("Profil kaydedilemedi", ex);
                RaiseLost(LostMessage);
                return false;
            }
        }
    }

    private void CheckAccess()
    {
        if (_disposed || _lost) return;
        bool exists;
        lock (_gate) exists = File.Exists(FilePath);
        if (!exists) RaiseLost(LostMessage);
    }

    private void RaiseLost(string reason)
    {
        if (_lost) return;
        _lost = true;
        Lost?.Invoke(reason);
    }

    /// <summary>Profil dosyasını başka bir klasöre taşır; null ise varsayılan konuma (AppData).</summary>
    public void MoveTo(string? folder)
    {
        lock (_gate)
        {
            var target = folder == null ? DefaultPathFor(Header.Id) : ProfileFile.UniquePath(folder, Header.Name);
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var fs = Lock(target, FileMode.CreateNew);
            try
            {
                WriteAll(fs, Encode());
                // Yeni dosya doğrulanmadan eskisi silinmez.
                ProfileFile.ReadHeader(target);
            }
            catch
            {
                fs.Dispose();
                File.Delete(target);
                throw;
            }
            var old = _file;
            _file = fs;
            FilePath = target;
            _dirty = false;
            old.Dispose();
            try
            {
                File.Delete(old.Name);
            }
            catch (Exception ex)
            {
                Log.Error("Eski profil dosyası silinemedi", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        _debounce.Dispose();
        _watch.Dispose();
        lock (_gate)
        {
            try
            {
                _file.Dispose();
            }
            catch (IOException)
            {
                // Sürücü çıkarılmışsa kapatırken de hata verebilir.
            }
        }
        CryptographicOperations.ZeroMemory(_key);
    }
}

public sealed record ProfileEntry(string Path, string Name, string Id)
{
    public override string ToString() => Name;
}

/// <summary>Varsayılan konumdaki (AppData\NetChat\UserProfiles) profiller.</summary>
public static class ProfileCatalog
{
    public static List<ProfileEntry> List() =>
        ListIn(AppPaths.ProfilesDir).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Bu cihazda (varsayılan konumda) veya <paramref name="folder"/> klasöründe aynı adlı bir profil var mı?</summary>
    public static bool NameExists(string name, string? folder)
    {
        var entries = ListIn(AppPaths.ProfilesDir);
        if (folder != null) entries.AddRange(ListIn(folder));
        return entries.Any(p => string.Equals(p.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }

    private static List<ProfileEntry> ListIn(string folder)
    {
        var list = new List<ProfileEntry>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*" + ProfileFile.Extension))
            {
                try
                {
                    var h = ProfileFile.ReadHeader(file);
                    list.Add(new ProfileEntry(file, h.Name, h.Id));
                }
                catch (Exception ex)
                {
                    Log.Error("Profil okunamadı", ex);
                }
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
        return list;
    }
}
