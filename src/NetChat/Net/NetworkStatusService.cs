using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetChat.Net;

public enum ConnectionKind
{
    None,
    Wired,
    Wireless,
}

public sealed record NetworkStatus(ConnectionKind Kind, string? AdapterName, string? Address)
{
    public bool IsConnected => Kind != ConnectionKind.None;
}

/// <summary>Cihazın yerel ağa nasıl bağlı olduğunu (Wi-Fi / kablolu / yok) izler.</summary>
public sealed class NetworkStatusService : IDisposable
{
    private static readonly string[] VirtualHints =
        ["virtual", "hyper-v", "vmware", "virtualbox", "vethernet", "loopback", "tap-", "tunnel", "wsl", "bluetooth", "npcap", "pseudo", "teredo"];

    private Timer? _debounce;

    public NetworkStatus Current { get; private set; } = Detect();

    public event Action<NetworkStatus>? Changed;

    public NetworkStatusService()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // Bağlantı değişirken birden çok olay gelir; kısa bir süre bekleyip tek seferde değerlendir.
        _debounce?.Dispose();
        _debounce = new Timer(_ =>
        {
            var status = Detect();
            if (status != Current)
            {
                Current = status;
                Changed?.Invoke(status);
            }
        }, null, 700, Timeout.Infinite);
    }

    public static NetworkStatus Detect()
    {
        try
        {
            var candidates = new List<(NetworkInterface Nic, string Address, bool HasGateway)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
                    continue;
                var desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
                if (VirtualHints.Any(desc.Contains)) continue;

                var props = nic.GetIPProperties();
                var ipv4 = props.UnicastAddresses.FirstOrDefault(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
                if (ipv4 == null) continue;

                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                candidates.Add((nic, ipv4.Address.ToString(), hasGateway));
            }

            var best = candidates.OrderByDescending(c => c.HasGateway).FirstOrDefault();
            if (best.Nic == null) return new NetworkStatus(ConnectionKind.None, null, null);

            var kind = best.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                ? ConnectionKind.Wireless
                : ConnectionKind.Wired;
            return new NetworkStatus(kind, best.Nic.Name, best.Address);
        }
        catch (NetworkInformationException)
        {
            return new NetworkStatus(ConnectionKind.None, null, null);
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _debounce?.Dispose();
    }
}
