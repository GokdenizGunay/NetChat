using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NetChat.Core;

/// <summary>
/// Cihaz kodu (ör. "spacecookie#2863"): bu bilgisayarı tanıtan, rastgele görünen kısa ad. Hiçbir yere kaydedilmez;
/// her açılışta donanımdan tek yönlü özetle yeniden türetilir. Kaynaklar, sırayla:
/// <list type="number">
/// <item>Anakart: SMBIOS sistem kimliği (UUID) ve seri numaraları. Her cihaza üretimde yazılır; disk kopyalansa
/// (Clonezilla vb.) ya da Windows yeniden kurulsa da değişmez.</item>
/// <item>Anakart bilgisi boş/sahteyse: Windows'un kurulu olduğu fiziksel diskin seri numarası.</item>
/// <item>İkisi de okunamazsa: Windows'un makine kimliği (MachineGuid).</item>
/// </list>
/// Donanım bilgisi koddan geri çıkarılamaz. Yönetici izni gerekmez.
/// </summary>
public static class DeviceCode
{
    private static readonly string[] First =
    [
        "space", "shadow", "silver", "amber", "lunar", "solar", "frost", "ember", "storm", "cloud",
        "pixel", "neon", "velvet", "crystal", "thunder", "misty", "golden", "cosmic", "rapid", "quiet",
        "swift", "wild", "brave", "lucky", "sunny", "stormy", "icy", "cozy", "fuzzy", "jolly",
        "crimson", "azure", "cobalt", "coral", "jade", "ruby", "onyx", "ivory", "maple", "cedar",
        "river", "ocean", "desert", "forest", "meadow", "canyon", "glacier", "island", "harbor", "summit",
        "turbo", "hyper", "mega", "ultra", "retro", "nova", "orbit", "rocket", "comet", "quantum",
        "echo", "zen", "drift", "spark",
    ];

    private static readonly string[] Second =
    [
        "cookie", "push", "fox", "otter", "falcon", "tiger", "panda", "koala", "raven", "wolf",
        "badger", "beaver", "dolphin", "penguin", "lynx", "hawk", "owl", "moose", "bison", "gecko",
        "muffin", "waffle", "pickle", "noodle", "pretzel", "taco", "mango", "lemon", "berry", "peach",
        "pebble", "boulder", "comet", "meteor", "planet", "nebula", "photon", "quasar", "rocket", "voyager",
        "anchor", "lantern", "compass", "beacon", "harbor", "bridge", "tower", "castle", "garden", "orchard",
        "wave", "breeze", "blaze", "flare", "spark", "pulse", "drift", "shift", "glide", "dash",
        "byte", "pixel", "socket", "circuit",
    ];

    private static readonly Lazy<(string Code, string Source)> Lazy = new(Compute);

    /// <summary>Bu cihazın kodu.</summary>
    public static string Current => Lazy.Value.Code;

    /// <summary>Uygulamada gösterilen biçim: cihaz kodu ve o cihazda açık profilin adı ("megacircuit#5543-elaa").</summary>
    public static string Label(string code, string profileName) => $"{code}-{profileName}";

    /// <summary>
    /// Kısa etiket: "#5543"; <paramref name="letters"/> &gt; 0 ise kodun numaradan önceki son harfleri büyük harfle
    /// başa eklenir ("T#5543", "IT#5543"). Aynı numaralı farklı cihazları ayırt etmek için.
    /// </summary>
    public static string Suffix(string code, int letters)
    {
        var hash = code.LastIndexOf('#');
        if (hash < 0) return "#" + code;
        var word = code[..hash];
        letters = Math.Clamp(letters, 0, word.Length);
        return word[^letters..].ToUpperInvariant() + code[hash..];
    }

    /// <summary>
    /// Verilen cihaz kodları için en kısa ayırt edici etiketler: numara tek başına yetiyorsa "#5543", aynı numaralı
    /// başka bir cihaz varsa yetecek kadar harf eklenir ("T#5543"). Aynı cihazdaki profiller aynı kodu paylaşır.
    /// </summary>
    public static Dictionary<string, string> Suffixes(IEnumerable<string> codes)
    {
        var distinct = codes.Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var code in distinct)
        {
            var letters = 0;
            var maxLetters = Math.Max(0, code.LastIndexOf('#'));
            while (letters < maxLetters && distinct.Any(other => other != code && Suffix(other, letters) == Suffix(code, letters)))
                letters++;
            result[code] = Suffix(code, letters);
        }
        return result;
    }

    /// <summary>Kodun hangi kaynaktan türetildiği ("anakart", "disk" ya da "windows"); günlük ve test için.</summary>
    public static string Source => Lazy.Value.Source;

    private static (string, string) Compute()
    {
        var (seed, source) = Board() is { } board ? ("board:" + board, "anakart")
            : SystemDiskSerial() is { } disk ? ("disk:" + disk, "disk")
            : ("windows:" + (MachineGuid() ?? Environment.MachineName), "windows");
        Log.Info("Cihaz kodu kaynağı: " + source);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("NetChat26/device/" + seed));
        var first = First[hash[0] % First.Length];
        var second = Second[hash[1] % Second.Length];
        var number = (BitConverter.ToUInt32(hash, 2) % 10000).ToString("D4");
        return ($"{first}{second}#{number}", source);
    }

    // ------------------------------------------------------------------ 1. Anakart (SMBIOS)

    /// <summary>Sahte/doldurulmamış sistem kimlikleri (bazı ucuz anakartlar hepsine aynısını yazar).</summary>
    private static readonly HashSet<Guid> PlaceholderUuids =
    [
        Guid.Empty,
        new("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"),
        new("03000200-0400-0500-0006-000700080009"),
        new("00020003-0004-0005-0006-000700080009"),
        new("12345678-1234-5678-90AB-CDDEEFAABBCC"),
    ];

    /// <summary>
    /// Anakart kimliği: geçerli sistem UUID'si ve geçerli seri numaraları birlikte. Hiçbiri geçerli değilse null.
    /// </summary>
    private static string? Board()
    {
        try
        {
            var table = ReadSmbios();
            if (table == null) return null;
            Guid? uuid = null;
            string? systemSerial = null, boardSerial = null;
            foreach (var (type, formatted, strings) in Structures(table))
            {
                if (type == 1 && formatted.Length >= 0x19)
                {
                    // Sistem bilgisi: 0x07 seri numarası (dize sırası), 0x08 UUID (16 bayt).
                    systemSerial = StringAt(strings, formatted[0x07]);
                    var guid = new Guid(formatted.AsSpan(0x08, 16));
                    if (!PlaceholderUuids.Contains(guid)) uuid = guid;
                }
                else if (type == 2 && formatted.Length >= 0x08)
                {
                    // Anakart bilgisi: 0x07 seri numarası.
                    boardSerial ??= StringAt(strings, formatted[0x07]);
                }
            }
            var parts = new List<string>();
            if (uuid is { } u) parts.Add("uuid=" + u.ToString("N"));
            if (IsRealSerial(systemSerial)) parts.Add("sys=" + systemSerial!.Trim());
            if (IsRealSerial(boardSerial)) parts.Add("board=" + boardSerial!.Trim());
            return parts.Count > 0 ? string.Join('|', parts) : null;
        }
        catch (Exception ex)
        {
            Log.Error("Anakart bilgisi okunamadı", ex);
            return null;
        }
    }

    private const uint Rsmb = 0x52534D42; // 'RSMB'

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);

    /// <summary>Ham SMBIOS tablosu (RawSMBIOSData başlığından sonraki kısım).</summary>
    private static byte[]? ReadSmbios()
    {
        var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size < 8) return null;
        var buffer = new byte[size];
        if (GetSystemFirmwareTable(Rsmb, 0, buffer, size) != size) return null;
        var length = BitConverter.ToInt32(buffer, 4);
        if (length <= 0 || 8 + length > buffer.Length) return null;
        return buffer[8..(8 + length)];
    }

    /// <summary>SMBIOS yapıları: tür, biçimli alan (başlık dahil) ve dizeler.</summary>
    private static IEnumerable<(byte Type, byte[] Formatted, List<string> Strings)> Structures(byte[] table)
    {
        var i = 0;
        while (i + 4 <= table.Length)
        {
            var type = table[i];
            var length = table[i + 1];
            if (length < 4 || i + length > table.Length) yield break;
            var formatted = table[i..(i + length)];
            // Dizeler biçimli alandan sonra gelir; her biri sıfırla, liste çift sıfırla biter.
            var strings = new List<string>();
            var j = i + length;
            while (j < table.Length)
            {
                var end = Array.IndexOf(table, (byte)0, j);
                if (end < 0) yield break;
                if (end == j)
                {
                    j++;
                    break;
                }
                strings.Add(Encoding.ASCII.GetString(table, j, end - j));
                j = end + 1;
            }
            if (strings.Count == 0) j++; // dizesiz yapı "00 00" ile biter
            yield return (type, formatted, strings);
            if (type == 127) yield break; // tablo sonu
            i = j;
        }
    }

    private static string? StringAt(List<string> strings, byte index) =>
        index >= 1 && index <= strings.Count ? strings[index - 1] : null;

    /// <summary>Üreticinin doldurmadığı yer tutucu seri numaraları.</summary>
    private static readonly string[] PlaceholderSerials =
    [
        "to be filled by o.e.m.", "default string", "system serial number", "base board serial number",
        "not specified", "not applicable", "none", "n/a", "na", "oem", "o.e.m.", "invalid", "unknown",
        "chassis serial number", "serial number", "0123456789", "123456789", "1234567890",
    ];

    private static bool IsRealSerial(string? serial)
    {
        var s = serial?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length < 4) return false;
        if (PlaceholderSerials.Contains(s.ToLowerInvariant())) return false;
        // "0000000", "FFFFFF", "xxxxxx" gibi tek karakter tekrarları.
        return s.Distinct().Count() > 1;
    }

    // ------------------------------------------------------------------ 2. Windows'un kurulu olduğu disk

    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public int DeviceType;
        public int DeviceNumber;
        public int PartitionNumber;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize,
        byte[] output, int outputSize, out int returned, IntPtr overlapped);

    /// <summary>
    /// Windows'un kurulu olduğu fiziksel diskin seri numarası. Diskler erişim hakkı istenmeden (yalnızca sorgu için)
    /// açılır; yönetici izni gerekmez.
    /// </summary>
    private static string? SystemDiskSerial()
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\');
            if (string.IsNullOrEmpty(systemDrive)) return null;

            int diskNumber;
            using (var volume = OpenForQuery(@"\\.\" + systemDrive))
            {
                if (volume.IsInvalid) return null;
                var output = new byte[Marshal.SizeOf<StorageDeviceNumber>()];
                if (!DeviceIoControl(volume, IoctlStorageGetDeviceNumber, null, 0, output, output.Length, out _, IntPtr.Zero)) return null;
                diskNumber = BitConverter.ToInt32(output, 4);
            }

            using var disk = OpenForQuery(@"\\.\PhysicalDrive" + diskNumber);
            if (disk.IsInvalid) return null;
            // STORAGE_PROPERTY_QUERY: PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0).
            var query = new byte[12];
            var descriptor = new byte[1024];
            if (!DeviceIoControl(disk, IoctlStorageQueryProperty, query, query.Length, descriptor, descriptor.Length, out var returned, IntPtr.Zero)) return null;
            // STORAGE_DEVICE_DESCRIPTOR: 0x18 SerialNumberOffset.
            var offset = BitConverter.ToInt32(descriptor, 0x18);
            if (offset <= 0 || offset >= returned) return null;
            var end = Array.IndexOf(descriptor, (byte)0, offset);
            var serial = Encoding.ASCII.GetString(descriptor, offset, (end < 0 ? returned : end) - offset).Trim();
            return IsRealSerial(serial) ? serial : null;
        }
        catch (Exception ex)
        {
            Log.Error("Disk seri numarası okunamadı", ex);
            return null;
        }
    }

    private static SafeFileHandle OpenForQuery(string path) =>
        // Erişim hakkı 0: içerik okunmaz, yalnızca aygıt bilgisi sorgulanır.
        CreateFile(path, 0, 3 /* FILE_SHARE_READ | FILE_SHARE_WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);

    // ------------------------------------------------------------------ 3. Windows makine kimliği

    private static string? MachineGuid()
    {
        try
        {
            // 32 bit görünümünde değer olmayabilir; her zaman 64 bit kayıt defterinden oku.
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string is { Length: > 0 } guid ? guid : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("Makine kimliği okunamadı", ex);
            return null;
        }
    }
}
