using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using GearBoardBridge.Models;
using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;
using Windows.Devices.Midi;

namespace GearBoardBridge.Services;

/// <summary>
/// USB MIDI transport — enumerates Windows MIDI input/output ports and bridges
/// MIDI data between GearBoard (connected via USB) and the virtual MIDI port.
///
/// Uses Windows.Devices.Midi for port access and DeviceWatcher for hot-plug support.
/// Estimated latency: ~2 ms.
/// </summary>
public sealed class UsbMidiService : IMidiTransport
{
    private readonly ILogger<UsbMidiService> _logger;

    private DeviceWatcher? _deviceWatcher;
    private MidiInPort?    _inPort;
    private IMidiOutPort?  _outPort;

    // ── IMidiTransport ────────────────────────────────────────────────────────
    public string TransportName    => "USB MIDI";
    public TransportType Type      => TransportType.USB;
    public double EstimatedLatencyMs => 2.0;

    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public string? ConnectedDeviceName { get; private set; }

    public ObservableCollection<DeviceInfo> DiscoveredDevices { get; } = [];

    public event Action<ConnectionState>? StateChanged;
    public event Action<byte[]>?          MidiDataReceived;
    public event Action<DeviceInfo>?      DeviceDiscovered;

    public UsbMidiService(ILogger<UsbMidiService> logger) => _logger = logger;

    // ── Scanning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Start a DeviceWatcher on the MIDI input selector.
    /// The watcher fires Added for all currently connected devices (initial enumeration)
    /// and then continues to watch for hot-plug add/remove events.
    /// </summary>
    public Task<bool> StartDiscoveryAsync()
    {
        // Stop any previous watcher before creating a new one
        if (_deviceWatcher != null)
        {
            _deviceWatcher.Added              -= OnDeviceAdded;
            _deviceWatcher.Removed            -= OnDeviceRemoved;
            _deviceWatcher.EnumerationCompleted -= OnEnumerationCompleted;
            _deviceWatcher.Stop();
            _deviceWatcher = null;
        }

        System.Windows.Application.Current?.Dispatcher.Invoke(DiscoveredDevices.Clear);
        SetState(ConnectionState.Scanning);
        _logger.LogInformation("USB MIDI scan started");

        var selector = MidiInPort.GetDeviceSelector();
        _deviceWatcher = DeviceInformation.CreateWatcher(selector);
        _deviceWatcher.Added              += OnDeviceAdded;
        _deviceWatcher.Removed            += OnDeviceRemoved;
        _deviceWatcher.EnumerationCompleted += OnEnumerationCompleted;
        _deviceWatcher.Start();

        return Task.FromResult(true);
    }

    /// <summary>Stop the DeviceWatcher.</summary>
    public Task StopDiscoveryAsync()
    {
        if (_deviceWatcher != null)
        {
            _deviceWatcher.Added              -= OnDeviceAdded;
            _deviceWatcher.Removed            -= OnDeviceRemoved;
            _deviceWatcher.EnumerationCompleted -= OnEnumerationCompleted;
            _deviceWatcher.Stop();
            _deviceWatcher = null;
        }

        if (State == ConnectionState.Scanning)
            SetState(ConnectionState.Idle);

        _logger.LogInformation("USB MIDI scan stopped");
        return Task.CompletedTask;
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation d)
    {
        var device = MakeDeviceInfo(d.Name, d.Id);
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!DiscoveredDevices.Any(x => x.Id == d.Id))
                DiscoveredDevices.Add(device);
        });
        DeviceDiscovered?.Invoke(device);
        _logger.LogDebug("USB MIDI device added: {Name}", d.Name);
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate d)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var existing = DiscoveredDevices.FirstOrDefault(x => x.Id == d.Id);
            if (existing != null) DiscoveredDevices.Remove(existing);
        });
        _logger.LogDebug("USB MIDI device removed: {Id}", d.Id);
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        _logger.LogInformation("USB MIDI initial enumeration complete — {Count} device(s) found",
            DiscoveredDevices.Count);
        // Stay in Scanning state while the watcher remains active (hot-plug)
    }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Open a MidiInPort for receiving and attempt to open a matching MidiOutPort
    /// for bidirectional communication (clock sync from DAW to phone).
    /// </summary>
    public async Task<bool> ConnectAsync(DeviceInfo device)
    {
        SetState(ConnectionState.Connecting);
        _logger.LogInformation("Connecting to USB MIDI device: {Name}", device.Name);

        try
        {
            await StopDiscoveryAsync();

            // ── Open input port ──
            _inPort = await MidiInPort.FromIdAsync(device.Id);
            if (_inPort == null)
            {
                _logger.LogWarning("Could not open MIDI input port for {Name}", device.Name);
                SetState(ConnectionState.Error);
                return false;
            }

            _inPort.MessageReceived += OnMidiMessageReceived;

            // ── Open matching output port (best-effort for DAW → phone) ──
            var outSelector = MidiOutPort.GetDeviceSelector();
            var outDevices  = await DeviceInformation.FindAllAsync(outSelector);
            var outDevice   = outDevices.FirstOrDefault(d =>
                d.Name.Equals(device.Name, StringComparison.OrdinalIgnoreCase));

            if (outDevice != null)
            {
                _outPort = await MidiOutPort.FromIdAsync(outDevice.Id);
                _logger.LogDebug("USB MIDI output port opened: {Name}", outDevice.Name);
            }
            else
            {
                _logger.LogInformation("No matching USB MIDI output port for {Name} (send disabled)", device.Name);
            }

            ConnectedDeviceName = device.Name;
            SetState(ConnectionState.Connected);
            _logger.LogInformation("Connected to USB MIDI: {Name}", device.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USB MIDI connection failed");
            SetState(ConnectionState.Error);
            return false;
        }
    }

    /// <summary>Close input and output ports and release resources.</summary>
    public Task DisconnectAsync()
    {
        if (_inPort != null)
        {
            _inPort.MessageReceived -= OnMidiMessageReceived;
            _inPort.Dispose();
            _inPort = null;
        }

        _outPort?.Dispose();
        _outPort = null;

        ConnectedDeviceName = null;
        SetState(ConnectionState.Idle);
        _logger.LogInformation("USB MIDI disconnected");
        return Task.CompletedTask;
    }

    // ── MIDI Data ─────────────────────────────────────────────────────────────

    private void OnMidiMessageReceived(MidiInPort sender, MidiMessageReceivedEventArgs args)
    {
        try
        {
            var raw = args.Message.RawData.ToArray();
            if (raw.Length > 0)
                MidiDataReceived?.Invoke(raw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing USB MIDI message");
        }
    }

    /// <summary>Send raw MIDI bytes to the connected device via the output port.</summary>
    public Task SendMidiAsync(byte[] midiData)
    {
        if (_outPort == null)
        {
            _logger.LogDebug("SendMidiAsync: no USB output port available");
            return Task.CompletedTask;
        }

        try
        {
            _outPort.SendBuffer(midiData.AsBuffer());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send USB MIDI data");
        }

        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetState(ConnectionState newState)
    {
        State = newState;
        StateChanged?.Invoke(newState);
    }

    private static DeviceInfo MakeDeviceInfo(string name, string id) =>
        new(Name:      name,
            Id:        id,
            Transport: TransportType.USB,
            IsGearBoard: name.Contains("GearBoard", StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        _deviceWatcher?.Stop();
        _inPort?.Dispose();
        _outPort?.Dispose();
    }
}
