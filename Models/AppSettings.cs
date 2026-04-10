namespace GearBoardBridge.Models;

/// <summary>
/// Persisted user settings — serialized to JSON at
/// %AppData%\Local\GearBoardBridge\settings.json.
/// </summary>
public class AppSettings
{
    // ── Auto-Connect ─────────────────────────────────────────────────────────

    /// <summary>Automatically connect to the last device on startup.</summary>
    public bool AutoConnectOnStartup { get; set; } = true;

    /// <summary>Display name of the last successfully connected device.</summary>
    public string? LastDeviceName { get; set; }

    /// <summary>Device ID (BLE address string, USB device id, or WiFi IP) of the last device.</summary>
    public string? LastDeviceId { get; set; }

    /// <summary>Transport used for the last successful connection.</summary>
    public TransportType? LastTransportType { get; set; }

    // ── Startup ───────────────────────────────────────────────────────────────

    /// <summary>Start the app minimized to the system tray.</summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>Launch GearBoard Bridge automatically when Windows starts.</summary>
    public bool StartWithWindows { get; set; } = false;

    // ── Virtual MIDI ──────────────────────────────────────────────────────────

    /// <summary>Name of the virtual MIDI port shown in DAWs.</summary>
    public string VirtualPortName { get; set; } = "GearBoard MIDI";

    // ── WiFi MIDI ─────────────────────────────────────────────────────────────

    /// <summary>UDP port used for WiFi MIDI discovery and data.</summary>
    public int WifiPort { get; set; } = 5004;

    // ── Transport Priority ────────────────────────────────────────────────────

    /// <summary>
    /// Preferred priority mode for automatic transport selection.
    /// Default is Auto (USB → WiFi → BLE).
    /// </summary>
    public TransportPriorityMode TransportPriority { get; set; } = TransportPriorityMode.Auto;

    // ── Window State ──────────────────────────────────────────────────────────

    /// <summary>Saved window left position (NaN = use default center-screen).</summary>
    public double WindowLeft { get; set; } = double.NaN;

    /// <summary>Saved window top position (NaN = use default center-screen).</summary>
    public double WindowTop { get; set; } = double.NaN;
}

/// <summary>
/// Determines which transport is preferred when multiple are available.
/// </summary>
public enum TransportPriorityMode
{
    /// <summary>USB → WiFi → BLE (default, lowest latency first).</summary>
    Auto,

    /// <summary>WiFi → USB → BLE.</summary>
    WifiFirst,

    /// <summary>BLE → WiFi → USB (useful when USB cable is for charging only).</summary>
    BleFirst,
}
