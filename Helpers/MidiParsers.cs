namespace GearBoardBridge.Helpers;

/// <summary>
/// Parses BLE MIDI packets to/from raw MIDI bytes.
/// Matches the packet format used by BleMidiPeripheral.kt in the Android app.
///
/// BLE MIDI format: [header] [timestamp] [midi_bytes...]
///   header:    0x80 | (timestamp_high and 0x3F)
///   timestamp: 0x80 | (timestamp_low and 0x7F)
/// </summary>
public static class BleMidiParser
{
    /// <summary>
    /// Parse a BLE MIDI notification packet, stripping header and timestamp.
    /// Returns raw MIDI bytes (status + data).
    /// </summary>
    public static byte[]? ParseBlePacket(byte[] packet)
    {
        if (packet.Length < 3) return null;

        // BLE MIDI format: [header] [timestamp] [midi_bytes...]
        // Skip first 2 bytes (header + timestamp)
        var midiData = new byte[packet.Length - 2];
        Array.Copy(packet, 2, midiData, 0, midiData.Length);
        return midiData;
    }

    /// <summary>
    /// Wrap raw MIDI bytes in BLE MIDI packet format for sending to peripheral.
    /// </summary>
    public static byte[] WrapMidiData(byte[] midiBytes)
    {
        var timestamp = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 8192);
        var header = (byte)(0x80 | ((timestamp >> 7) & 0x3F));
        var timestampLow = (byte)(0x80 | (timestamp & 0x7F));

        var packet = new byte[2 + midiBytes.Length];
        packet[0] = header;
        packet[1] = timestampLow;
        Array.Copy(midiBytes, 0, packet, 2, midiBytes.Length);
        return packet;
    }
}

/// <summary>
/// Parses raw MIDI bytes into MidiMessage objects for the monitor UI.
/// </summary>
public static class MidiParser
{
    public static Models.MidiMessage? Parse(byte[] data, Models.MidiDirection direction, Models.TransportType transport)
    {
        if (data.Length == 0) return null;

        var statusByte = data[0] & 0xFF;

        // System real-time (single byte)
        return statusByte switch
        {
            0xF8 => new Models.MidiMessage
            {
                Type = Models.MidiMessageType.Clock,
                Direction = direction,
                Transport = transport,
                RawBytes = data
            },
            0xFA => new Models.MidiMessage
            {
                Type = Models.MidiMessageType.Start,
                Direction = direction,
                Transport = transport,
                RawBytes = data
            },
            0xFB => new Models.MidiMessage
            {
                Type = Models.MidiMessageType.Continue,
                Direction = direction,
                Transport = transport,
                RawBytes = data
            },
            0xFC => new Models.MidiMessage
            {
                Type = Models.MidiMessageType.Stop,
                Direction = direction,
                Transport = transport,
                RawBytes = data
            },
            _ => ParseChannelMessage(data, direction, transport)
        };
    }

    private static Models.MidiMessage? ParseChannelMessage(byte[] data, Models.MidiDirection direction, Models.TransportType transport)
    {
        if (data.Length < 2) return null;

        var status = data[0] & 0xFF;
        var channel = status & 0x0F;
        var msgType = status & 0xF0;
        var d1 = data.Length >= 2 ? data[1] & 0x7F : 0;
        var d2 = data.Length >= 3 ? data[2] & 0x7F : 0;

        var type = msgType switch
        {
            0x80 => Models.MidiMessageType.NoteOff,
            0x90 => Models.MidiMessageType.NoteOn,
            0xB0 => Models.MidiMessageType.ControlChange,
            0xC0 => Models.MidiMessageType.ProgramChange,
            0xE0 => Models.MidiMessageType.PitchBend,
            _ => Models.MidiMessageType.Unknown
        };

        return new Models.MidiMessage
        {
            Type = type,
            Channel = channel,
            Data1 = d1,
            Data2 = d2,
            Direction = direction,
            Transport = transport,
            RawBytes = data
        };
    }
}
