using System.Collections.ObjectModel;
using GearBoardBridge.Models;

namespace GearBoardBridge.Services;

/// <summary>
/// Common interface for all MIDI transports (BLE, USB, WiFi).
/// MidiBridge and TransportManager work with this abstraction.
/// </summary>
public interface IMidiTransport : IDisposable
{
    /// <summary>Display name, e.g. "BLE MIDI", "USB MIDI", "WiFi MIDI".</summary>
    string TransportName { get; }

    /// <summary>Transport type enum.</summary>
    TransportType Type { get; }

    /// <summary>Current connection state.</summary>
    ConnectionState State { get; }

    /// <summary>Name of connected device (null if not connected).</summary>
    string? ConnectedDeviceName { get; }

    /// <summary>Estimated round-trip latency in milliseconds.</summary>
    double EstimatedLatencyMs { get; }

    /// <summary>Devices discovered during scanning.</summary>
    ObservableCollection<DeviceInfo> DiscoveredDevices { get; }

    /// <summary>Start scanning/watching for devices.</summary>
    Task<bool> StartDiscoveryAsync();

    /// <summary>Stop scanning.</summary>
    Task StopDiscoveryAsync();

    /// <summary>Connect to a discovered device.</summary>
    Task<bool> ConnectAsync(DeviceInfo device);

    /// <summary>Disconnect from current device.</summary>
    Task DisconnectAsync();

    /// <summary>Send raw MIDI bytes to the connected device.</summary>
    Task SendMidiAsync(byte[] midiData);

    /// <summary>Fired when connection state changes.</summary>
    event Action<ConnectionState>? StateChanged;

    /// <summary>Fired when raw MIDI data is received from the device.</summary>
    event Action<byte[]>? MidiDataReceived;

    /// <summary>Fired when a new device is discovered during scanning.</summary>
    event Action<DeviceInfo>? DeviceDiscovered;
}
