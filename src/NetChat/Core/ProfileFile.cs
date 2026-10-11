using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetChat.Core;

/// <summary>Profil dosyasının şifrelenmemiş başlığı (giriş ekranında adı göstermek için).</summary>
public sealed class ProfileHeader
{
    public int V { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public string Kdf { get; set; } = "pbkdf2-sha256";
    public int Iterations { get; set; } = ProfileFile.DefaultIterations;
    public string Salt { get; set; } = "";
}

/// <summary>Şifre çözülmüş profil içeriği.</summary>
public sealed class ProfilePayload
{
    public byte[] IdentityPkcs8 { get; set; } = [];
    public byte[]? SettingsJson { get; set; }
    public byte[]? StoreJson { get; set; }
}

public sealed class WrongPasswordException() : Exception("Şifre hatalı.");

public sealed class InvalidProfileException(string message) : Exception(message);

/// <summary>
/// .nprofile biçimi — yalnızca NetChat'in açabileceği şifreli kapsayıcı:
/// <code>
/// "NCPROFILE" 0x01 | uint16 başlık uzunluğu | başlık (JSON) | nonce (12) | etiket (16) | şifreli veri
/// </code>
/// Anahtar: PBKDF2-SHA256 (600 000 tur, 16 bayt tuz) ile şifreden türetilir.
/// Veri: kimlik anahtarı + ayarlar + sohbet geçmişi, Brotli ile sıkıştırılıp AES-256-GCM ile şifrelenir.
/// Başlık AAD olarak doğrulanır; adı veya tuzu değiştirilmiş dosya açılmaz.
/// </summary>
public static class ProfileFile
{
    public const string Extension = ".nprofile";
    public const int DefaultIterations = 600_000;
    private static readonly byte[] Magic = "NCPROFILE\u0001"u8.ToArray();

    public static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    public static ProfileHeader NewHeader(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Created = DateTimeOffset.Now,
        Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
    };

    public static ProfileHeader ReadHeader(string path)
    {
        // Açık oturumun kilitli dosyası da okunabilsin (oturum yazarken yalnızca okumaya izin verir).
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadHeader(fs, out _);
    }

    private static ProfileHeader ReadHeader(Stream s, out byte[] aad)
    {
        var magic = new byte[Magic.Length];
        s.ReadExactly(magic);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidProfileException("Bu dosya bir NetChat profili değil.");
        var lenBytes = new byte[2];
        s.ReadExactly(lenBytes);
        var len = BinaryPrimitives.ReadUInt16BigEndian(lenBytes);
        var headerBytes = new byte[len];
        s.ReadExactly(headerBytes);
        aad = [.. magic, .. lenBytes, .. headerBytes];
        return JsonSerializer.Deserialize<ProfileHeader>(headerBytes) ?? throw new InvalidProfileException("Profil başlığı okunamadı.");
    }

    /// <summary>Dosyayı açar ve şifresini çözer. Şifre yanlışsa <see cref="WrongPasswordException"/>.</summary>
    public static (ProfileHeader Header, ProfilePayload Payload, byte[] Key) Open(byte[] data, string password)
    {
        using var ms = new MemoryStream(data);
        var header = ReadHeader(ms, out var aad);
        if (header.Kdf != "pbkdf2-sha256" || header.Iterations < 100_000)
            throw new InvalidProfileException("Desteklenmeyen profil sürümü.");
        var key = DeriveKey(password, Convert.FromBase64String(header.Salt), header.Iterations);
        var payload = Decrypt(data, (int)ms.Position, aad, key);
        return (header, payload, key);
    }

    private static ProfilePayload Decrypt(byte[] data, int offset, byte[] aad, byte[] key)
    {
        var nonce = data.AsSpan(offset, 12);
        var tag = data.AsSpan(offset + 12, 16);
        var cipher = data.AsSpan(offset + 28);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new WrongPasswordException();
        }

        using var input = new BrotliStream(new MemoryStream(plain), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<ProfilePayload>(input) ?? throw new InvalidProfileException("Profil içeriği bozuk.");
    }

    /// <summary>Paketi şifreleyip dosya içeriğini üretir.</summary>
    public static byte[] Encode(ProfileHeader header, ProfilePayload payload, byte[] key)
    {
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
        var lenBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenBytes, (ushort)headerBytes.Length);
        byte[] aad = [.. Magic, .. lenBytes, .. headerBytes];

        byte[] plain;
        using (var ms = new MemoryStream())
        {
            using (var brotli = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                JsonSerializer.Serialize(brotli, payload);
            plain = ms.ToArray();
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag, aad);
        CryptographicOperations.ZeroMemory(plain);
        return [.. aad, .. nonce, .. tag, .. cipher];
    }

    /// <summary>Klasörde çakışmayan bir dosya adı üretir ("Ad.nprofile", "Ad (2).nprofile"…).</summary>
    public static string UniquePath(string folder, string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safe.Length == 0) safe = "profil";
        var path = Path.Combine(folder, safe + Extension);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{safe} ({i}){Extension}");
        return path;
    }
}
