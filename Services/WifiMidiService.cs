using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GearBoardBridge.Models;
using Microsoft.Extensions.Logging;
using TransportType = GearBoardBridge.Models.TransportType;

namespace GearBoardBridge.Services;

/// <summary>
/// WiFi MIDI transport — discovers GearBoard devices via UDP broadcast,
/// then exchanges raw MIDI bytes over UDP sockets.
///
/// Discovery protocol:
///   Android broadcasts JSON to 255.255.255.255:5004 →
///   {"name":"GearBoard","ip":"&lt;android-ip&gt;","midiPort":5006}
///
/// MIDI exchange:
///   Android → Windows : UDP datagrams sent to Windows port 5005
///   Windows → Android : UDP datagrams sent to Android's midiPort (default 5006)
///
/// Estimated latency: ~5 ms on a local Wi-Fi network.
/// </summary>
public sealed class WifiMidiService : IMidiTransport
{
    private const int DiscoveryPort = 5004;
    private const int ReceivePort   = 5005;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<WifiMidiService> _logger;

    private UdpClient?               _discoveryClient;
    private UdpClient?               _receiveClient;
    private UdpClient?               _sendClient;
    private CancellationTokenSource? _discoveryCts;
    private CancellationTokenSource? _receiveCts;

    // ── IMidiTransport ────────────────────────────────────────────────────────
    public string TransportName    => "WiFi MIDI";
    public TransportType Type      => TransportType.WiFi;
    public double EstimatedLatencyMs => 5.0;

    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public string? ConnectedDeviceName { get; private set; }

    public ObservableCollection<DeviceInfo> DiscoveredDevices { get; } = [];

    public event Action<ConnectionState>? StateChanged;
    public event Action<byte[]>?          MidiDataReceived;
    public event Action<DeviceInfo>?      DeviceDiscovered;

    public WifiMidiService(ILogger<WifiMidiService> logger) => _logger = logger;

    // ── Scanning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Bind to the discovery broadcast port and listen for GearBoard announcements.
    /// Discovery runs until <see cref="StopDiscoveryAsync"/> is called.
    /// </summary>
    public Task<bool> StartDiscoveryAsync()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(DiscoveredDevices.Clear);
        SetState(ConnectionState.Scanning);
        _logger.LogInformation("WiFi MIDI discovery started (port {Port})", DiscoveryPort);

        _discoveryCts = new CancellationTokenSource();
        _ = RunDiscoveryLoopAsync(_discoveryCts.Token);

        return Task.FromResult(true);
    }

    /// <summary>Stop listening for broadcast announcements.</summary>
    public Task StopDiscoveryAsync()
    {
        _discoveryCts?.Cancel();
        _discoveryCts = null;

        // Closing the socket unblocks any pending ReceiveAsync
        _discoveryClient?.Close();
        _discoveryClient = null;

        if (State == ConnectionState.Scanning)
            SetState(ConnectionState.Idle);

        _logger.LogInformation("WiFi MIDI discovery stopped");
        return Task.CompletedTask;
    }

    private async Task RunDiscoveryLoopAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        try
        {
            _discoveryClient = new UdpClient();
            _discoveryClient.Client.SetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _discoveryClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _discoveryClient.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bind discovery socket on port {Port}", DiscoveryPort);
            SetState(ConnectionState.Idle);
            return;
        }

        var seen = new HashSet<string>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _discoveryClient.ReceiveAsync(ct);
                var json   = Encoding.UTF8.GetString(result.Buffer);

                var packet = JsonSerializer.Deserialize<DiscoveryPacket>(json, JsonOpts);
                if (packet == null || string.IsNullOrWhiteSpace(packet.Ip)) continue;

                var port = packet.MidiPort > 0 ? packet.MidiPort : DiscoveryPort;
                var id   = $"{packet.Ip}:{port}";
                if (!seen.Add(id)) continue;

                var name = string.IsNullOrWhiteSpace(packet.Name)
                    ? $"WiFi Device ({packet.Ip})"
                    : packet.Name;

                var device = new DeviceInfo(
                    Name:       name,
                    Id:         id,
                    Transport:  TransportType.WiFi,
                    IsGearBoard: name.Contains("GearBoard", StringComparison.OrdinalIgnoreCase));

                _logger.LogDebug("WiFi MIDI device found: {Name} at {Id}", name, id);

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (!DiscoveredDevices.Any(d => d.Id == id))
                        DiscoveredDevices.Add(device);
                });
                DeviceDiscovered?.Invoke(device);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException)    { break; }
            catch (SocketException)            { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing WiFi discovery packet");
            }
        }

        _logger.LogDebug("WiFi discovery loop exited");
    }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Establish a UDP connection to the GearBoard device identified by
    /// <c>device.Id</c> ("&lt;ip&gt;:&lt;port&gt;") and start the receive loop.
    /// </summary>
    public async Task<bool> ConnectAsync(DeviceInfo device)
    {
        SetState(ConnectionState.Connecting);
        _logger.LogInformation("Connecting to WiFi MIDI device: {Name} ({Id})", device.Name, device.Id);

        try
        {
            await StopDiscoveryAsync();

            // Parse "ip:port" from device.Id
            var parts = device.Id.Split(':');
            if (parts.Length != 2
                || !IPAddress.TryParse(parts[0], out var ip)
                || !int.TryParse(parts[1], out var remotePort))
            {
                _logger.LogWarning("Invalid WiFi device ID: {Id}", device.Id);
                SetState(ConnectionState.Error);
                return false;
            }

            var remoteEndpoint = new IPEndPoint(ip, remotePort);

            // ── Send socket: Windows → Android ──
            _sendClient = new UdpClient();
            _sendClient.Connect(remoteEndpoint);

            // ── Receive socket: Android → Windows ──
            _receiveClient = new UdpClient();
            _receiveClient.Client.SetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _receiveClient.Client.Bind(new IPEndPoint(IPAddress.Any, ReceivePort));

            _receiveCts = new CancellationTokenSource();
            _ = RunReceiveLoopAsync(_receiveCts.Token);

            // Send connect handshake (best-effort; Android may ignore it)
            try
            {
                var handshake = Encoding.UTF8.GetBytes("{\"event\":\"connect\"}");
                await _sendClient.SendAsync(handshake, handshake.Length);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Connect handshake failed (non-fatal)");
            }

            ConnectedDeviceName = device.Name;
            SetState(ConnectionState.Connected);
            _logger.LogInformation("Connected to WiFi MIDI: {Name} at {Endpoint}", device.Name, remoteEndpoint);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WiFi MIDI connection failed");
            SetState(ConnectionState.Error);
            return false;
        }
    }

    /// <summary>Stop the receive loop, send a disconnect notification, and release sockets.</summary>
    public Task DisconnectAsync()
    {
        _receiveCts?.Cancel();
        _receiveCts = null;

        _receiveClient?.Close();
        _receiveClient = null;

        if (_sendClient != null)
        {
            try
            {
                var bye = Encoding.UTF8.GetBytes("{\"event\":\"disconnect\"}");
                _sendClient.Send(bye, bye.Length);
            }
            catch { /* best-effort */ }

            _sendClient.Close();
            _sendClient = null;
        }

        ConnectedDeviceName = null;
        SetState(ConnectionState.Idle);
        _logger.LogInformation("WiFi MIDI disconnected");
        return Task.CompletedTask;
    }

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("WiFi receive loop started on port {Port}", ReceivePort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _receiveClient!.ReceiveAsync(ct);
                if (result.Buffer.Length > 0)
                    MidiDataReceived?.Invoke(result.Buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException)    { break; }
            catch (SocketException)            { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in WiFi receive loop");
            }
        }

        _logger.LogDebug("WiFi receive loop exited");
    }

    // ── MIDI Data ─────────────────────────────────────────────────────────────

    /// <summary>Send raw MIDI bytes to the connected GearBoard device.</summary>
    public async Task SendMidiAsync(byte[] midiData)
    {
        if (_sendClient == null)
        {
            _logger.LogDebug("SendMidiAsync: WiFi not connected");
            return;
        }

        try
        {
            await _sendClient.SendAsync(midiData, midiData.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send WiFi MIDI data");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetState(ConnectionState newState)
    {
        State = newState;
        StateChanged?.Invoke(newState);
    }

    /// <summary>JSON payload broadcast by the Android GearBoard app on the local network.</summary>
    private sealed class DiscoveryPacket
    {
        public string Name     { get; set; } = "";
        public string Ip       { get; set; } = "";
        public int    MidiPort { get; set; } = DiscoveryPort;
    }

    public void Dispose()
    {
        _discoveryCts?.Cancel();
        _receiveCts?.Cancel();
        _discoveryClient?.Close();
        _receiveClient?.Close();
        _sendClient?.Close();
    }
}
