namespace GearBoardBridge.Models;

/// <summary>
/// Results of system requirements check performed on first launch.
/// </summary>
public class SystemCheckResult
{
    // Windows
    public string WindowsVersion { get; set; } = "";
    public bool IsWindows10OrLater { get; set; }
    public bool IsWindows11 { get; set; }

    // Bluetooth / BLE
    public bool HasBluetoothAdapter { get; set; }
    public string BluetoothAdapterName { get; set; } = "";
    public bool IsBluetoothEnabled { get; set; }
    public bool HasBleSupport { get; set; }

    // USB
    public bool HasUsbMidiDevice { get; set; }
    public string UsbMidiDeviceName { get; set; } = "";

    // WiFi
    public bool HasWifiConnection { get; set; }
    public string WifiNetworkName { get; set; } = "";
    public string LocalIpAddress { get; set; } = "";

    // Virtual MIDI
    public bool HasVirtualMidiDriver { get; set; }
    public bool HasWindowsMidiServices { get; set; }

    /// <summary>
    /// True if at least one transport is available and virtual MIDI can be created.
    /// </summary>
    public bool AllRequirementsMet =>
        IsWindows10OrLater &&
        (HasBluetoothAdapter || HasUsbMidiDevice || HasWifiConnection) &&
        (HasVirtualMidiDriver || HasWindowsMidiServices);

    /// <summary>
    /// True if at least one transport can work (even without virtual MIDI).
    /// </summary>
    public bool AnyTransportAvailable =>
        HasBleSupport || HasUsbMidiDevice || HasWifiConnection;
}
