using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using GearBoardBridge.Helpers;
using GearBoardBridge.Models;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace GearBoardBridge.Services;

/// <summary>
/// BLE MIDI transport — connects to a GearBoard Android device advertising
/// the BLE MIDI service UUID and bridges MIDI data via notifications/writes.
///
/// Matches the UUID pair in BleMidiPeripheral.kt:
///   Service:        03B80E5A-EDE8-4B33-A751-6CE34EC4C700
///   Characteristic: 7772E5DB-3868-4112-A1A9-F2669D106BF3
/// </summary>
public sealed class BleMidiService : IMidiTransport
{
    // ── BLE MIDI UUIDs ────────────────────────────────────────────────────────
    private static readonly Guid MidiServiceUuid = Guid.Parse("03B80E5A-EDE8-4B33-A751-6CE34EC4C700");
    private static readonly Guid MidiCharUuid    = Guid.Parse("7772E5DB-3868-4112-A1A9-F2669D106BF3");

    private readonly ILogger<BleMidiService> _logger;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice?  _bleDevice;
    private GattCharacteristic? _midiChar;

    // Deduplicate scan results by BLE address
    private readonly HashSet<ulong> _seenAddresses = [];

    // ── IMidiTransport ────────────────────────────────────────────────────────
    public string TransportName => "BLE MIDI";
    public TransportType Type   => TransportType.BLE;
    public double EstimatedLatencyMs => 15;

    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public string? ConnectedDeviceName { get; private set; }

    public ObservableCollection<DeviceInfo> DiscoveredDevices { get; } = [];

    public event Action<ConnectionState>? StateChanged;
    public event Action<byte[]>?          MidiDataReceived;
    public event Action<DeviceInfo>?      DeviceDiscovered;

    public BleMidiService(ILogger<BleMidiService> logger) => _logger = logger;

    // ── Scanning ──────────────────────────────────────────────────────────────

    /// <summary>Start BLE advertisement scan filtered to the MIDI service UUID.</summary>
    public Task<bool> StartDiscoveryAsync()
    {
        _seenAddresses.Clear();

        System.Windows.Application.Current?.Dispatcher.Invoke(DiscoveredDevices.Clear);

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        // Only surface devices advertising the BLE MIDI service UUID
        _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(MidiServiceUuid);
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped  += OnWatcherStopped;
        _watcher.Start();

        SetState(ConnectionState.Scanning);
        _logger.LogInformation("BLE scan started");
        return Task.FromResult(true);
    }

    /// <summary>Stop advertisement scanning.</summary>
    public Task StopDiscoveryAsync()
    {
        if (_watcher != null)
        {
            _watcher.Received -= OnAdvertisementReceived;
            _watcher.Stopped  -= OnWatcherStopped;
            _watcher.Stop();
            _watcher = null;
        }

        if (State == ConnectionState.Scanning)
            SetState(ConnectionState.Idle);

        _logger.LogInformation("BLE scan stopped");
        return Task.CompletedTask;
    }

    private void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // Deduplicate by address
        if (!_seenAddresses.Add(args.BluetoothAddress)) return;

        var name = args.Advertisement.LocalName;
        if (string.IsNullOrWhiteSpace(name))
            name = $"BLE Device ({args.BluetoothAddress:X12})";

        var device = new DeviceInfo(
            Name:           name,
            Id:             args.BluetoothAddress.ToString("X12"),
            Transport:      TransportType.BLE,
            IsGearBoard:    name.Contains("GearBoard", StringComparison.OrdinalIgnoreCase),
            SignalStrength: (short)args.RawSignalStrengthInDBm
        ) { BleAddress = args.BluetoothAddress };

        _logger.LogDebug("BLE device found: {Name} RSSI={Rssi}", name, args.RawSignalStrengthInDBm);

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            DiscoveredDevices.Add(device);
        });

        DeviceDiscovered?.Invoke(device);
    }

    private void OnWatcherStopped(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        _logger.LogDebug("BLE watcher stopped. Error={Error}", args.Error);
    }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Connect to a discovered BLE MIDI device:
    /// 1. Resolve address → BluetoothLEDevice
    /// 2. Find MIDI service
    /// 3. Find MIDI characteristic
    /// 4. Subscribe to notifications (CCCD write)
    /// </summary>
    public async Task<bool> ConnectAsync(DeviceInfo device)
    {
        SetState(ConnectionState.Connecting);
        _logger.LogInformation("Connecting to BLE device: {Name}", device.Name);

        try
        {
            await StopDiscoveryAsync();

            // ── 1. Resolve BLE device ──
            _bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(device.BleAddress);
            if (_bleDevice == null)
            {
                _logger.LogWarning("Could not resolve BLE address {Addr:X12}", device.BleAddress);
                SetState(ConnectionState.Error);
                return false;
            }

            // ── 2. Find the BLE MIDI service ──
            var servicesResult = await _bleDevice.GetGattServicesForUuidAsync(
                MidiServiceUuid, BluetoothCacheMode.Uncached);

            if (servicesResult.Status != GattCommunicationStatus.Success
                || servicesResult.Services.Count == 0)
            {
                _logger.LogWarning("MIDI service not found on {Name} (status={Status})",
                    device.Name, servicesResult.Status);
                SetState(ConnectionState.Error);
                return false;
            }

            var midiService = servicesResult.Services[0];

            // ── 3. Find the MIDI characteristic ──
            var charsResult = await midiService.GetCharacteristicsForUuidAsync(
                MidiCharUuid, BluetoothCacheMode.Uncached);

            if (charsResult.Status != GattCommunicationStatus.Success
                || charsResult.Characteristics.Count == 0)
            {
                _logger.LogWarning("MIDI characteristic not found on {Name}", device.Name);
                SetState(ConnectionState.Error);
                return false;
            }

            _midiChar = charsResult.Characteristics[0];

            // ── 4. Enable CCCD notifications ──
            var notifyStatus = await _midiChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if (notifyStatus != GattCommunicationStatus.Success)
            {
                _logger.LogWarning("Failed to enable MIDI notifications: {Status}", notifyStatus);
                SetState(ConnectionState.Error);
                return false;
            }

            _midiChar.ValueChanged += OnMidiValueChanged;
            _bleDevice.ConnectionStatusChanged += OnConnectionStatusChanged;

            ConnectedDeviceName = device.Name;
            SetState(ConnectionState.Connected);
            _logger.LogInformation("Connected to {Name}", device.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BLE connection failed");
            SetState(ConnectionState.Error);
            return false;
        }
    }

    /// <summary>Gracefully disconnect: disable CCCD, release device.</summary>
    public async Task DisconnectAsync()
    {
        if (_midiChar != null)
        {
            _midiChar.ValueChanged -= OnMidiValueChanged;
            try
            {
                // Tell peripheral to stop sending notifications
                await _midiChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CCCD disable failed (device may already be disconnected)");
            }
            _midiChar = null;
        }

        if (_bleDevice != null)
        {
            _bleDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _bleDevice.Dispose();
            _bleDevice = null;
        }

        ConnectedDeviceName = null;
        SetState(ConnectionState.Idle);
        _logger.LogInformation("BLE disconnected");
    }

    // ── MIDI Data ─────────────────────────────────────────────────────────────

    private void OnMidiValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var raw      = args.CharacteristicValue.ToArray();
            var midiData = BleMidiParser.ParseBlePacket(raw);
            if (midiData != null)
                MidiDataReceived?.Invoke(midiData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing BLE MIDI packet");
        }
    }

    /// <summary>
    /// Send raw MIDI bytes to the connected peripheral.
    /// Wraps bytes in BLE MIDI packet format before writing.
    /// Used for DAW → GearBoard clock sync.
    /// </summary>
    public async Task SendMidiAsync(byte[] midiData)
    {
        if (_midiChar == null)
        {
            _logger.LogWarning("SendMidiAsync called but not connected");
            return;
        }

        try
        {
            var packet = BleMidiParser.WrapMidiData(midiData);
            var buffer = packet.AsBuffer();
            var status = await _midiChar.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
            if (status != GattCommunicationStatus.Success)
                _logger.LogWarning("MIDI write failed: {Status}", status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending MIDI data");
        }
    }

    // ── Connection Status ─────────────────────────────────────────────────────

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _logger.LogWarning("BLE device dropped connection unexpectedly");
            ConnectedDeviceName = null;
            SetState(ConnectionState.Error);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetState(ConnectionState newState)
    {
        State = newState;
        StateChanged?.Invoke(newState);
    }

    public void Dispose()
    {
        _watcher?.Stop();
        _bleDevice?.Dispose();
    }
}
