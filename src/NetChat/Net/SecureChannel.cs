using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetChat.Core;

namespace NetChat.Net;

/// <summary>Bağlantı kurulurken iki tarafın açık olarak gönderdiği tanıtım mesajı.</summary>
public sealed class HelloInfo
{
    public string Proto { get; set; } = SecureChannel.ProtocolName;
    public string Pub { get; set; } = "";
    public string Eph { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Name { get; set; } = "";
    public int Port { get; set; }
}

/// <summary>
/// Uçtan uca şifreli TCP kanalı.
///
/// El sıkışma:
///  1. Her iki taraf kalıcı kimlik açık anahtarını (ECDSA P-256), bu bağlantıya özel geçici
///     ECDH P-256 açık anahtarını ve rastgele bir nonce'u gönderir.
///  2. Transcript = SHA-256(başlatanın hello'su || yanıtlayanın hello'su).
///  3. Her taraf transcript'i kimlik anahtarıyla imzalar; karşı taraf imzayı doğrular.
///     Kimlik Id'si açık anahtarın özetinden türetildiği için sahte kimlik kullanılamaz.
///  4. Geçici ECDH ortak sırrından HKDF-SHA256 ile her yön için ayrı 256-bit AES anahtarı türetilir
///     (ileri gizlilik / forward secrecy: oturum anahtarları diske hiç yazılmaz).
///
/// Çerçeveler: [4 bayt uzunluk][AES-256-GCM şifreli veri][16 bayt etiket].
/// Nonce, her yönde artan 64-bit sayaçtır; tekrar oynatma ve sıra değiştirme saldırıları
/// kimlik doğrulama hatasıyla sonuçlanır.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    public const string ProtocolName = "NetChat26/1";
    public const int MaxFrame = 1024 * 1024 + 4096;
    private const int TagSize = 16;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly AesGcm _sendAes;
    private readonly AesGcm _recvAes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _readHeader = new byte[4];
    private ulong _sendCounter;
    private ulong _recvCounter;
    private bool _disposed;

    public string PeerId { get; }
    public byte[] PeerPublicKey { get; }
    public HelloInfo PeerHello { get; }
    public System.Net.IPAddress RemoteAddress { get; }
    public bool IsInitiator { get; }

    private SecureChannel(TcpClient client, byte[] sendKey, byte[] recvKey, string peerId, byte[] peerPub, HelloInfo hello, bool initiator)
    {
        _client = client;
        _stream = client.GetStream();
        _sendAes = new AesGcm(sendKey, TagSize);
        _recvAes = new AesGcm(recvKey, TagSize);
        PeerId = peerId;
        PeerPublicKey = peerPub;
        PeerHello = hello;
        IsInitiator = initiator;
        RemoteAddress = ((System.Net.IPEndPoint)client.Client.RemoteEndPoint!).Address;
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(recvKey);
    }

    public static async Task<SecureChannel> EstablishAsync(
        TcpClient client, Identity me, string myName, int myPort, bool initiator, string? expectedPeerId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var stream = client.GetStream();

        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var myHello = new HelloInfo
        {
            Pub = Convert.ToBase64String(me.PublicKey),
            Eph = Convert.ToBase64String(eph.ExportSubjectPublicKeyInfo()),
            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Name = myName,
            Port = myPort,
        };
        var myHelloBytes = JsonSerializer.SerializeToUtf8Bytes(myHello);

        await WriteRawAsync(stream, myHelloBytes, token);
        var peerHelloBytes = await ReadRawAsync(stream, 8192, token);
        var peerHello = JsonSerializer.Deserialize<HelloInfo>(peerHelloBytes)
                        ?? throw new InvalidDataException("Geçersiz hello");
        if (peerHello.Proto != ProtocolName)
            throw new InvalidDataException($"Desteklenmeyen protokol: {peerHello.Proto}");

        var peerPub = Convert.FromBase64String(peerHello.Pub);
        var peerId = Identity.IdFromPublicKey(peerPub);
        if (peerId == me.Id) throw new InvalidDataException("Kendine bağlantı");
        if (expectedPeerId != null && peerId != expectedPeerId)
            throw new CryptographicException("Karşı tarafın kimliği beklenenle eşleşmiyor");

        var transcript = SHA256.HashData(initiator
            ? Concat(myHelloBytes, peerHelloBytes)
            : Concat(peerHelloBytes, myHelloBytes));

        // Kimlik doğrulama: transcript + rol baytı imzalanır.
        var mySig = me.SigningKey.SignData(Concat(transcript, [initiator ? (byte)1 : (byte)2]), HashAlgorithmName.SHA256);
        await WriteRawAsync(stream, mySig, token);
        var peerSig = await ReadRawAsync(stream, 256, token);

        using (var peerKey = ECDsa.Create())
        {
            peerKey.ImportSubjectPublicKeyInfo(peerPub, out _);
            if (!peerKey.VerifyData(Concat(transcript, [initiator ? (byte)2 : (byte)1]), peerSig, HashAlgorithmName.SHA256))
                throw new CryptographicException("El sıkışma imzası doğrulanamadı");
        }

        using var peerEph = ECDiffieHellman.Create();
        peerEph.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerHello.Eph), out _);
        var shared = eph.DeriveRawSecretAgreement(peerEph.PublicKey);
        var i2r = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, transcript, "NetChat26 i2r"u8.ToArray());
        var r2i = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, transcript, "NetChat26 r2i"u8.ToArray());
        CryptographicOperations.ZeroMemory(shared);

        return initiator
            ? new SecureChannel(client, i2r, r2i, peerId, peerPub, peerHello, true)
            : new SecureChannel(client, r2i, i2r, peerId, peerPub, peerHello, false);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        if (plaintext.Length > MaxFrame - TagSize) throw new ArgumentException("Çerçeve çok büyük");
        var frame = new byte[4 + plaintext.Length + TagSize];
        BinaryPrimitives.WriteInt32BigEndian(frame, plaintext.Length + TagSize);

        await _writeLock.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var nonce = MakeNonce(_sendCounter++);
            _sendAes.Encrypt(nonce, plaintext.Span,
                frame.AsSpan(4, plaintext.Length),
                frame.AsSpan(4 + plaintext.Length, TagSize),
                frame.AsSpan(0, 4));
            await _stream.WriteAsync(frame, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Bir sonraki çerçeveyi okur ve çözer. Bağlantı kapanırsa null döner.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            await _stream.ReadExactlyAsync(_readHeader, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var len = BinaryPrimitives.ReadInt32BigEndian(_readHeader);
        if (len < TagSize || len > MaxFrame) throw new InvalidDataException($"Geçersiz çerçeve uzunluğu: {len}");
        var body = new byte[len];
        await _stream.ReadExactlyAsync(body, ct);

        var plain = new byte[len - TagSize];
        var nonce = MakeNonce(_recvCounter++);
        _recvAes.Decrypt(nonce, body.AsSpan(0, plain.Length), body.AsSpan(plain.Length, TagSize), plain, _readHeader);
        return plain;
    }

    private static byte[] MakeNonce(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    private static async Task WriteRawAsync(Stream stream, byte[] data, CancellationToken ct)
    {
        var buf = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        data.CopyTo(buf, 4);
        await stream.WriteAsync(buf, ct);
    }

    private static async Task<byte[]> ReadRawAsync(Stream stream, int max, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len <= 0 || len > max) throw new InvalidDataException("Geçersiz el sıkışma çerçevesi");
        var data = new byte[len];
        await stream.ReadExactlyAsync(data, ct);
        return data;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _client.Close(); } catch { }
        _sendAes.Dispose();
        _recvAes.Dispose();
    }

    public override string ToString() => $"{PeerHello.Name} ({PeerId[..8]}) @ {RemoteAddress}";
}

internal static class Utf8
{
    public static readonly UTF8Encoding NoBom = new(false);
}
