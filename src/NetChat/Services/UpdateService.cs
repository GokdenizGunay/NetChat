using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetChat.Core;

namespace NetChat.Services;

/// <summary>GitHub'daki daha yeni bir yayın.</summary>
public sealed record UpdateInfo(Version Version, string DownloadUrl, long Size, string? Sha256, string PageUrl);

/// <summary>
/// Otomatik güncelleme: GitHub'daki son yayını denetler, yeni sürümün .exe'sini indirir, doğrular ve
/// çalışan exe'nin yerine koyar. Çalışan exe silinemez ama yeniden adlandırılabilir: önce ".old.exe" yapılır,
/// yeni exe aynı ada taşınır, yeni sürüm "--updated-from" ile başlatılır ve eski exe'yi o siler.
/// </summary>
public sealed class UpdateService
{
    private const string DefaultReleaseUrl = "https://api.github.com/repos/GokdenizGunay/NetChat/releases/latest";

    /// <summary>Denetlenen adres; yerel deneme için NETCHAT_UPDATE_FEED ortam değişkeniyle değiştirilebilir.</summary>
    private static string LatestReleaseUrl => Environment.GetEnvironmentVariable("NETCHAT_UPDATE_FEED") ?? DefaultReleaseUrl;
    private const string ProductName = "NetChat 26";

    /// <summary>Bu kadar süre hiç veri gelmezse bağlantı kopmuş sayılır (sessizce donan bağlantılar hata vermez).</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    // Sıra önemli: istemci, kimlik başlığında sürümü kullandığı için sürümden sonra oluşturulur.
    public static Version CurrentVersion { get; } = Normalize(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Yalnızca yayımlanmış tek dosyalık exe güncellenebilir (geliştirme derlemeleri değil).</summary>
    public static bool IsSupported =>
        Environment.ProcessPath is { } path && string.IsNullOrEmpty(typeof(App).Assembly.Location)
        && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bulunan daha yeni sürüm; yoksa null.</summary>
    public UpdateInfo? Available
    {
        get => _available;
        private set
        {
            if (_available == value) return;
            _available = value;
            AvailableChanged?.Invoke();
        }
    }
    private UpdateInfo? _available;

    /// <summary>Yeni sürüm bulundu ya da artık yok (denetimi başlatan iş parçacığında gelir).</summary>
    public event Action? AvailableChanged;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"NetChat/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Etiketten sürüm (ör. "26-v1.1.0" → 1.1.0).</summary>
    public static Version? ParseTag(string tag)
    {
        var match = Regex.Matches(tag, @"(\d+)\.(\d+)\.(\d+)").LastOrDefault();
        return match == null ? null : new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
    }

    /// <summary>
    /// Son yayını denetler; yeni sürüm varsa <see cref="Available"/> dolar. Denetim yapılamadıysa (ağ yok, yayın okunamadı)
    /// false döner.
    /// </summary>
    public async Task<bool> CheckAsync()
    {
        if (!IsSupported) return false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await Http.GetAsync(LatestReleaseUrl, cts.Token);
            if (!response.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            if (ParseTag(root.GetProperty("tag_name").GetString() ?? "") is not { } version) return false;
            if (version <= CurrentVersion)
            {
                Available = null;
                return true;
            }

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                string? sha = asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:")
                    ? d["sha256:".Length..]
                    : null;
                Available = new UpdateInfo(version, asset.GetProperty("browser_download_url").GetString() ?? "",
                    asset.GetProperty("size").GetInt64(), sha, root.GetProperty("html_url").GetString() ?? "");
                Log.Info($"Yeni sürüm bulundu: {version}");
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Info("Güncelleme denetlenemedi: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Yeni exe'yi geçici klasöre indirir ve doğrular; indirilen dosyanın yolunu döndürür.
    /// <see cref="IdleTimeout"/> boyunca veri gelmezse <see cref="TimeoutException"/> fırlatır.
    /// Başarısız olursa yarım dosya silinir.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<(long Received, long Total)> progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NetChat-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"NetChat-{update.Version}.exe");

        // Her veri parçasında yeniden kurulan "hareketsizlik" zamanlayıcısı.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        try
        {
            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, idle.Token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? update.Size;
                await using var input = await response.Content.ReadAsStreamAsync(idle.Token);
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, idle.Token)) > 0)
                {
                    idle.CancelAfter(IdleTimeout);
                    await output.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                    received += read;
                    progress.Report((received, total));
                }
            }
            Verify(target, update);
            return target;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(target);
            throw new TimeoutException("Bağlantı kesildi: bir süredir veri gelmiyor.");
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Yarım güncelleme dosyası silinemedi", ex);
        }
    }

    private static void Verify(string path, UpdateInfo update)
    {
        var length = new FileInfo(path).Length;
        if (update.Size > 0 && length != update.Size) throw new InvalidDataException("İndirilen dosya eksik.");
        if (update.Sha256 != null)
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("İndirilen dosya doğrulanamadı.");
        }
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName != ProductName || ParseTag(info.ProductVersion ?? "") != update.Version)
            throw new InvalidDataException("İndirilen dosya beklenen NetChat sürümü değil.");
    }

    /// <summary>
    /// Yeni exe'yi çalışan exe'nin yerine koyar ve başlatır. Başarılıysa çağıran uygulamayı kapatmalıdır.
    /// Klasöre yazılamıyorsa (ör. Program Files) hata fırlatır ve hiçbir şeyi değiştirmez.
    /// </summary>
    public static void InstallAndStart(string downloaded)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Uygulama yolu bulunamadı.");
        var old = Path.Combine(Path.GetDirectoryName(current)!, Path.GetFileNameWithoutExtension(current) + ".old.exe");
        if (File.Exists(old)) File.Delete(old);

        File.Move(current, old);
        try
        {
            File.Move(downloaded, current);
        }
        catch
        {
            File.Move(old, current);
            throw;
        }

        var start = new ProcessStartInfo(current) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(current)! };
        start.ArgumentList.Add("--updated-from");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(old);
        if (AppPaths.Profile != "default")
        {
            start.ArgumentList.Add("--profile");
            start.ArgumentList.Add(AppPaths.Profile);
        }
        if (AppPaths.LoggingEnabled) start.ArgumentList.Add("--log");
        Process.Start(start);
    }

    /// <summary>
    /// Güncellemeden sonra yeni sürümde: eski sürüm kapanınca eski exe'yi siler.
    /// Silmeden önce okunan eski sürümü döndürür (bilinmiyorsa null).
    /// </summary>
    public static Version? FinishUpdate(int oldPid, string oldExe)
    {
        try
        {
            using var old = Process.GetProcessById(oldPid);
            old.WaitForExit(15000);
        }
        catch (ArgumentException)
        {
            // Zaten kapanmış.
        }
        Version? previous = null;
        try
        {
            if (File.Exists(oldExe)) previous = ParseTag(FileVersionInfo.GetVersionInfo(oldExe).ProductVersion ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        for (int i = 0; i < 20 && File.Exists(oldExe); i++)
        {
            try
            {
                File.Delete(oldExe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
        if (File.Exists(oldExe)) Log.Error("Eski sürüm silinemedi: " + oldExe);
        else Log.Info($"Güncelleme tamamlandı: {CurrentVersion}");
        return previous;
    }
}
