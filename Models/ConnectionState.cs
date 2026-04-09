namespace GearBoardBridge.Models;

/// <summary>
/// Connection state for any MIDI transport.
/// </summary>
public enum ConnectionState
{
    Idle,
    Scanning,
    Connecting,
    Connected,
    Reconnecting,
    Error
}

/// <summary>
/// Available transport types, ordered by priority.
/// </summary>
public enum TransportType
{
    USB,    // ~2ms latency, lowest
    WiFi,   // ~3-8ms latency
    BLE     // ~10-20ms latency
}

/// <summary>
/// Direction of MIDI message flow.
/// </summary>
public enum MidiDirection
{
    PhoneToDaw,  // GearBoard → Ableton
    DawToPhone   // Ableton → GearBoard (clock sync)
}
