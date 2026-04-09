namespace GearBoardBridge.Models;

/// <summary>
/// Parsed MIDI message for display in the monitor.
/// </summary>
public record MidiMessage
{
    public MidiMessageType Type { get; init; }
    public int Channel { get; init; }
    public int Data1 { get; init; }
    public int Data2 { get; init; }
    public MidiDirection Direction { get; init; }
    public TransportType Transport { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public byte[] RawBytes { get; init; } = [];

    /// <summary>Human-readable display string for the MIDI monitor.</summary>
    public string DisplayText => Type switch
    {
        MidiMessageType.ControlChange => $"CC  ch{Channel + 1} #{Data1} val={Data2}",
        MidiMessageType.ProgramChange => $"PC  ch{Channel + 1} prg={Data1}",
        MidiMessageType.NoteOn => $"NOn ch{Channel + 1} {NoteToName(Data1)} vel={Data2}",
        MidiMessageType.NoteOff => $"NOff ch{Channel + 1} {NoteToName(Data1)}",
        MidiMessageType.Clock => "CLK ♩",
        MidiMessageType.Start => "START",
        MidiMessageType.Stop => "STOP",
        MidiMessageType.Continue => "CONT",
        MidiMessageType.PitchBend => $"PB  ch{Channel + 1} val={Data1 | (Data2 << 7)}",
        _ => $"??? {string.Join(" ", RawBytes.Select(b => b.ToString("X2")))}"
    };

    /// <summary>Short type label for colored display in the MIDI monitor.</summary>
    public string TypeLabel => Type switch
    {
        MidiMessageType.ControlChange => "CC",
        MidiMessageType.ProgramChange => "PC",
        MidiMessageType.NoteOn        => "NOn",
        MidiMessageType.NoteOff       => "NOff",
        MidiMessageType.Clock         => "CLK",
        MidiMessageType.Start         => "START",
        MidiMessageType.Stop          => "STOP",
        MidiMessageType.Continue      => "CONT",
        MidiMessageType.PitchBend     => "PB",
        _                             => "???"
    };

    /// <summary>Detail portion of the message (separate from type label) for colored display.</summary>
    public string DetailText => Type switch
    {
        MidiMessageType.ControlChange => $"  ch{Channel + 1} #{Data1} val={Data2}",
        MidiMessageType.ProgramChange => $"  ch{Channel + 1} prg={Data1}",
        MidiMessageType.NoteOn        => $"  ch{Channel + 1} {NoteToName(Data1)} vel={Data2}",
        MidiMessageType.NoteOff       => $"  ch{Channel + 1} {NoteToName(Data1)}",
        MidiMessageType.Clock         => "  ♩=---",
        MidiMessageType.Start         => "",
        MidiMessageType.Stop          => "",
        MidiMessageType.Continue      => "",
        MidiMessageType.PitchBend     => $"  ch{Channel + 1} val={Data1 | (Data2 << 7)}",
        _                             => $"  {string.Join(" ", RawBytes.Select(b => b.ToString("X2")))}"
    };

    /// <summary>Hex color string for the type label per Figma spec.</summary>
    public string TypeColorHex => Type switch
    {
        MidiMessageType.ControlChange => "#E8A020",  // amber
        MidiMessageType.ProgramChange => "#CE93D8",  // purple
        MidiMessageType.Clock         => "#42A5F5",  // blue
        MidiMessageType.NoteOn        => "#4CAF50",  // green
        MidiMessageType.NoteOff       => "#4CAF50",  // green
        _                             => "#B0B0C0"   // secondary
    };

    public string DirectionArrow => Direction == MidiDirection.PhoneToDaw ? "→" : "←";
    public string TimeString => Timestamp.ToString("HH:mm:ss");

    private static string NoteToName(int note)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[note % 12]}{note / 12 - 1}";
    }
}

public enum MidiMessageType
{
    NoteOff,
    NoteOn,
    ControlChange,
    ProgramChange,
    PitchBend,
    Clock,
    Start,
    Stop,
    Continue,
    SongPositionPointer,
    Unknown
}
