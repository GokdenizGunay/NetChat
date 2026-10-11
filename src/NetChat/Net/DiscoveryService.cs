using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetChat.Core;

namespace NetChat.Net;

/// <summary>Yerel ağda yayınlanan UDP keşif paketi.</summary>
public sealed class Beacon
{
    public string App { get; set; } = "NetChat26";
    public int V { get; set; } = 1;
    /// <summary>hello: periyodik duyuru, probe: herkesten hemen yanıt iste, bye: çıkış.</summary>
    public string T { get; set; } = "hello";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public string Pub { get; set; } = "";
    /// <summary>Durum: "online" (pencere önde), "idle" (boşta) veya "hidden" (paylaşılmıyor).</summary>
    public string? St { get; set; }
    /// <summary>Son aktiflik (Unix ms); paylaşılmıyorsa null.</summary>
    public long? La { get; set; }
    /// <summary>Oturum kimliği: aynı profilin ağda ikinci kez açıldığını ayırt etmek için.</summary>
    public string? Sid { get; set; }
    /// <summary>Oturumun başladığı an (Unix ms); çakışmada son açılan oturum kapanır.</summary>
    public long? Ss { get; set; }
    /// <summary>Durum bilgisinin gizlendiği (engellenen) kişiler için etiketler. Kimlikler açıkça yazılmaz.</summary>
    public List<string>? Hx { get; set; }
    /// <summary>Gruplara ekleme izni: null = herkes, "none", "only" (yalnızca Gx), "except" (Gx hariç).</summary>
    public string? Ga { get; set; }
    /// <summary>Ga listesindeki kişiler için etiketler (Hx gibi; kimlikler açıkça yazılmaz).</summary>
    public List<string>? Gx { get; set; }
}

public sealed record LocalPresence(string State, long? LastActive, IReadOnlyCollection<string> HideFrom,
    string? GroupMode = null, IReadOnlyCollection<string>? GroupTags = null);

public sealed record DiscoveredPeer(string Id, string Name, IPAddress Address, int Port, string PublicKey,
    string? State, long? LastActive, bool HidesFromMe, bool CanAddMe);

/// <summary>
/// UDP yayını (broadcast) ile eş keşfi. Her birkaç saniyede bir duyuru yapar; gelen duyuruların
/// Id'sinin açık anahtarla eşleştiğini doğrular. Asıl kimlik doğrulaması TCP el sıkışmasında yapılır.
///
/// Giriş sırasında önce "sessiz" başlar: kendini duyurmadan ağı dinler (aynı profil/aynı ad denetimi),
/// ardından <see cref="BeginAnnouncing"/> ile ağa katılır.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    public const int Port = 45454;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);

    private readonly Identity _identity;
    private readonly Func<string> _getName;
    private readonly Func<int> _getTcpPort;
    private readonly Func<LocalPresence> _getPresence;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private readonly long _sessionStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private UdpClient? _udp;
    private bool _announcing;

    public event Action<DiscoveredPeer>? PeerSeen;
    public event Action<string>? PeerLeft;

    /// <summary>Aynı profil ağda daha önce açılmış başka bir oturumda çalışıyor (bu oturum kapanmalı).</summary>
    public event Action? DuplicateSession;

    public DiscoveryService(Identity identity, Func<string> getName, Func<int> getTcpPort, Func<LocalPresence> getPresence)
    {
        _identity = identity;
        _getName = getName;
        _getTcpPort = getTcpPort;
        _getPresence = getPresence;
    }

    /// <summary>Engellenen kişiye özel, tek yönlü etiket: yalnızca o kişi kendi kimliğiyle eşleştirebilir.</summary>
    public static string HideTag(string fromId, string toId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"NetChat26/hide/{fromId}/{toId}")), 0, 8);

    /// <summary>Grup ekleme listesindeki kişiye özel etiket (yalnızca o kişi kendi kimliğiyle eşleştirebilir).</summary>
    public static string GroupTag(string fromId, string toId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"NetChat26/group/{fromId}/{toId}")), 0, 8);

    /// <summary>Dinlemeye başlar ve ağdakilerden hemen yanıt ister; kendini henüz duyurmaz.</summary>
    public void StartListening()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        _udp = udp;
        _ = ReceiveLoopAsync(udp, _cts.Token);
        _ = SendAsync("probe");
    }

    /// <summary>Ağa katılır: periyodik duyurular başlar.</summary>
    public void BeginAnnouncing()
    {
        if (_announcing) return;
        _announcing = true;
        _ = AnnounceLoopAsync(_cts.Token);
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        await SendAsync("hello");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            await SendAsync("hello");
        }
    }

    /// <summary>Ağı yeniden taramak için yanıt isteyen bir yayın gönderir.</summary>
    public Task ProbeAsync() => SendAsync("probe");

    public Task AnnounceAsync() => _announcing ? SendAsync("hello") : Task.CompletedTask;

    private async Task SendAsync(string type)
    {
        var udp = _udp;
        if (udp == null) return;
        var presence = _getPresence();
        var beacon = new Beacon
        {
            T = type,
            Id = _identity.Id,
            Name = _getName(),
            Port = _getTcpPort(),
            Pub = Convert.ToBase64String(_identity.PublicKey),
            St = presence.State,
            La = presence.LastActive,
            Sid = _sessionId,
            Ss = _sessionStart,
            Hx = presence.HideFrom.Count > 0 ? [.. presence.HideFrom] : null,
            Ga = presence.GroupMode,
            Gx = presence.GroupTags is { Count: > 0 } tags ? [.. tags] : null,
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(beacon, Wire.Json);

        foreach (var ep in GetBroadcastTargets())
        {
            try
            {
                await udp.SendAsync(data, ep);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Bazı arayüzler (ör. bağlantısı kopmuş) yayını reddedebilir.
            }
        }
    }

    /// <summary>Genel yayın adresi + her etkin arayüzün alt ağ yayın adresi.</summary>
    private static List<IPEndPoint> GetBroadcastTargets()
    {
        var list = new List<IPEndPoint> { new(IPAddress.Broadcast, Port) };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 0)) continue;
                    var bcast = new byte[4];
                    for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                    var ep = new IPEndPoint(new IPAddress(bcast), Port);
                    if (!list.Contains(ep)) list.Add(ep);
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        // Aynı makinedeki diğer örnekler için.
        list.Add(new IPEndPoint(IPAddress.Loopback, Port));
        return list;
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException)
            {
                // Windows, ICMP "port ulaşılamaz" yanıtlarını burada bildirebilir; yok say.
                continue;
            }

            try
            {
                Handle(result);
            }
            catch (Exception ex)
            {
                Log.Info($"Geçersiz keşif paketi ({result.RemoteEndPoint}): {ex.Message}");
            }
        }
    }

    private void Handle(UdpReceiveResult result)
    {
        if (result.Buffer.Length > 8192) return;
        var beacon = JsonSerializer.Deserialize<Beacon>(result.Buffer, Wire.Json);
        if (beacon == null || beacon.App != "NetChat26") return;

        var pub = Convert.FromBase64String(beacon.Pub);
        if (Identity.IdFromPublicKey(pub) != beacon.Id) return;

        if (beacon.Id == _identity.Id)
        {
            // Kendi yayınımız değil ama aynı profil: başka bir oturum. Önce açılan kalır, sonra açılan kapanır.
            // (Sonra açılan oturum ön kontrolde "probe" gönderir; ona yanıt olarak duyuru yaparız ve o kendini kapatır.)
            if (beacon.Sid != null && beacon.Sid != _sessionId)
            {
                if (beacon.T == "probe")
                {
                    if (_announcing) _ = SendAsync("hello");
                }
                else if (beacon.T == "hello" && (beacon.Ss ?? 0) <= _sessionStart)
                {
                    DuplicateSession?.Invoke();
                }
            }
            return;
        }

        if (beacon.T == "bye")
        {
            PeerLeft?.Invoke(beacon.Id);
            return;
        }

        var name = beacon.Name.Trim();
        if (name.Length > 40) name = name[..40];
        var hidesFromMe = beacon.Hx?.Contains(HideTag(beacon.Id, _identity.Id)) == true;
        var listed = beacon.Gx?.Contains(GroupTag(beacon.Id, _identity.Id)) == true;
        var canAddMe = beacon.Ga switch
        {
            "none" => false,
            "only" => listed,
            "except" => !listed,
            _ => true,
        };
        PeerSeen?.Invoke(new DiscoveredPeer(beacon.Id, name, result.RemoteEndPoint.Address, beacon.Port, beacon.Pub,
            beacon.St, beacon.La, hidesFromMe, canAddMe));

        // Probe'a yayınla yanıt ver ki yeni katılan (aynı makinedeki diğer örnekler dahil) beklemeden bizi görsün.
        if (beacon.T == "probe" && _announcing) _ = SendAsync("hello");
    }

    public void Dispose()
    {
        try
        {
            if (_announcing) SendAsync("bye").Wait(500);
        }
        catch
        {
        }
        _cts.Cancel();
        _udp?.Dispose();
    }
}
