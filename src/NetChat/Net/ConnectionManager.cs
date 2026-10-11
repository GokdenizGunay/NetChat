using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NetChat.Core;

namespace NetChat.Net;

/// <summary>
/// TCP dinleyici ve eşlere giden şifreli bağlantı havuzu.
/// Olaylar ağ iş parçacıklarında tetiklenir; arayüz tarafı Dispatcher'a aktarmalıdır.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    public const int PreferredPort = 45455;

    private readonly Identity _identity;
    private readonly Func<string> _getName;
    private readonly ConcurrentDictionary<string, SecureChannel> _channels = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectLocks = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public int Port { get; private set; }

    /// <summary>Kimliği doğrulanmış yeni bir kanal açıldı.</summary>
    public event Action<SecureChannel>? ChannelOpened;
    public event Action<SecureChannel>? ChannelClosed;
    public event Action<SecureChannel, Packet>? PacketReceived;
    public event Action<SecureChannel, Guid, long, ReadOnlyMemory<byte>>? ChunkReceived;

    /// <summary>Bağlanılacak adresi döndürür (keşif servisinden).</summary>
    public Func<string, IPEndPoint?>? ResolveEndpoint { get; set; }

    public ConnectionManager(Identity identity, Func<string> getName)
    {
        _identity = identity;
        _getName = getName;
    }

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, PreferredPort);
            _listener.Start();
        }
        catch (SocketException)
        {
            // Port meşgul (ör. aynı makinede ikinci profil): rastgele bir port kullan.
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Log.Info($"TCP dinleniyor: {Port}");
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Error("Accept hatası", ex);
                await Task.Delay(500, ct).ContinueWith(_ => { });
                continue;
            }
            _ = HandleIncomingAsync(client, ct);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            Configure(client);
            var channel = await SecureChannel.EstablishAsync(client, _identity, _getName(), Port, false, null, ct);
            Register(channel);
        }
        catch (Exception ex)
        {
            Log.Error("Gelen bağlantı reddedildi", ex);
            client.Dispose();
        }
    }

    public bool IsConnected(string peerId) => _channels.ContainsKey(peerId);

    /// <summary>Eşe açık bir kanal döndürür; yoksa bağlanmayı dener. Başarısızsa null.</summary>
    public async Task<SecureChannel?> GetChannelAsync(string peerId, CancellationToken ct = default)
    {
        if (_channels.TryGetValue(peerId, out var existing)) return existing;

        var gate = _connectLocks.GetOrAdd(peerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_channels.TryGetValue(peerId, out existing)) return existing;
            var endpoint = ResolveEndpoint?.Invoke(peerId);
            if (endpoint == null) return null;

            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                Configure(client);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                await client.ConnectAsync(endpoint, timeout.Token);
                var channel = await SecureChannel.EstablishAsync(client, _identity, _getName(), Port, true, peerId, _cts.Token);
                Register(channel);
                return channel;
            }
            catch (Exception ex)
            {
                Log.Info($"{peerId[..8]} eşine bağlanılamadı ({endpoint}): {ex.Message}");
                client.Dispose();
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Paketi gönderir. Başarılıysa true.</summary>
    public async Task<bool> SendAsync(string peerId, Packet packet, CancellationToken ct = default) =>
        await SendRawAsync(peerId, Wire.Encode(packet), ct);

    public async Task<bool> SendRawAsync(string peerId, byte[] frame, CancellationToken ct = default)
    {
        // Bir kez yeniden bağlanmayı dene (eski kanal kopmuş olabilir).
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var channel = await GetChannelAsync(peerId, ct);
            if (channel == null) return false;
            try
            {
                await channel.SendAsync(frame, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Info($"Gönderim hatası {channel}: {ex.Message}");
                Close(channel);
            }
        }
        return false;
    }

    private void Register(SecureChannel channel)
    {
        // Aynı eşe iki bağlantı aynı anda açılabilir; en yenisi gönderim için kullanılır,
        // eskisi okunmaya devam eder ve karşı taraf kapatana kadar yaşar.
        _channels[channel.PeerId] = channel;
        Log.Info($"Şifreli kanal açıldı: {channel} (başlatan: {channel.IsInitiator})");
        ChannelOpened?.Invoke(channel);
        _ = ReadLoopAsync(channel);
    }

    private async Task ReadLoopAsync(SecureChannel channel)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await channel.ReceiveAsync(_cts.Token);
                if (frame == null || frame.Length == 0) break;
                switch (frame[0])
                {
                    case Wire.KindJson:
                        Packet? packet;
                        try
                        {
                            packet = Wire.DecodeJson(frame);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Paket çözülemedi", ex);
                            continue;
                        }
                        if (packet != null) PacketReceived?.Invoke(channel, packet);
                        break;
                    case Wire.KindChunk:
                        var (id, offset, data) = Wire.DecodeChunk(frame);
                        ChunkReceived?.Invoke(channel, id, offset, data);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"Kanal kapandı {channel}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
        Close(channel);
    }

    private void Close(SecureChannel channel)
    {
        var removed = _channels.TryRemove(new KeyValuePair<string, SecureChannel>(channel.PeerId, channel));
        channel.Dispose();
        if (removed) ChannelClosed?.Invoke(channel);
    }

    public void Disconnect(string peerId)
    {
        if (_channels.TryGetValue(peerId, out var channel)) Close(channel);
    }

    private static void Configure(TcpClient client)
    {
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        foreach (var channel in _channels.Values) channel.Dispose();
        _channels.Clear();
    }
}
