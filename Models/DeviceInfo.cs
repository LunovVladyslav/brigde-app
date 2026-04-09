namespace GearBoardBridge.Models;

/// <summary>
/// Represents a discovered MIDI device (BLE, USB, or WiFi).
/// </summary>
public record DeviceInfo(
    string Name,
    string Id,
    TransportType Transport,
    bool IsGearBoard = false,
    short SignalStrength = 0
)
{
    /// <summary>BLE address (only for BLE devices).</summary>
    public ulong BleAddress { get; init; }

    public string TransportIcon => Transport switch
    {
        TransportType.USB => "🔌",
        TransportType.WiFi => "📶",
        TransportType.BLE => "📡",
        _ => "?"
    };

    public string DisplayName => $"{TransportIcon} {Name}";

    /// <summary>
    /// Second-line subtitle shown in the device list row (transport + signal/mode info).
    /// </summary>
    public string SubtitleText
    {
        get
        {
            string t = Transport switch
            {
                TransportType.USB  => "USB",
                TransportType.WiFi => "WiFi",
                TransportType.BLE  => "BLE",
                _                  => "?"
            };
            if (Transport == TransportType.USB)
                return $"{t}  •  Class-compliant MIDI";
            return SignalStrength switch
            {
                >= -60 => $"{t}  •  Signal: Strong",
                >= -80 => $"{t}  •  Signal: Medium",
                _      => $"{t}  •  Signal: Weak"
            };
        }
    }
}
