using System.Security.Cryptography;

namespace NetChat.Core;

/// <summary>
/// Cihazın kalıcı kimliği: bir ECDSA P-256 anahtar çifti.
/// Kullanıcı kimliği (Id) açık anahtarın SHA-256 özetinden türetilir; böylece bir kimlik
/// yalnızca ilgili özel anahtara sahip olan tarafından kullanılabilir.
/// Özel anahtar yalnızca şifreli profil paketinde saklanır.
/// </summary>
public sealed class Identity
{
    public ECDsa SigningKey { get; }
    public byte[] PublicKey { get; }
    public string Id { get; }

    private Identity(ECDsa key)
    {
        SigningKey = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        Id = IdFromPublicKey(PublicKey);
    }

    public static string IdFromPublicKey(byte[] spki) =>
        Convert.ToHexString(SHA256.HashData(spki), 0, 16).ToLowerInvariant();

    /// <summary>İnsan tarafından karşılaştırılabilir güvenlik kodu (WhatsApp'taki gibi).</summary>
    public static string Fingerprint(byte[] spki)
    {
        var hash = SHA256.HashData(spki);
        var digits = new System.Text.StringBuilder();
        for (int i = 0; i < 12; i++)
        {
            var chunk = BitConverter.ToUInt16(hash, i * 2) % 100000;
            if (i > 0) digits.Append(i % 4 == 0 ? "\n" : "  ");
            digits.Append(chunk.ToString("00000"));
        }
        return digits.ToString();
    }

    public static Identity CreateNew() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static Identity FromPkcs8(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new Identity(key);
    }

    public byte[] ExportPkcs8() => SigningKey.ExportPkcs8PrivateKey();
}
