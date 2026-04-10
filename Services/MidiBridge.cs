using GearBoardBridge.Helpers;
using GearBoardBridge.Models;
using Microsoft.Extensions.Logging;

namespace GearBoardBridge.Services;

/// <summary>
/// Routes MIDI bytes between the active transport and the virtual MIDI port.
///
/// Phone  →  ActiveTransport.MidiDataReceived  →  VirtualMidiPort.SendMidi  →  DAW
/// DAW    →  VirtualMidiPort.DawMidiReceived   →  ActiveTransport.SendMidiAsync  →  Phone
///
/// Fires <see cref="MidiMessageProcessed"/> for every message that passes through
/// so the UI MIDI monitor can display it.
/// </summary>
public sealed class MidiBridge : IDisposable
{
    private readonly TransportManager      _transportManager;
    private readonly VirtualMidiPortService _virtualPort;
    private readonly ILogger<MidiBridge>   _logger;

    // ── Stats ─────────────────────────────────────────────────────────────────

    private int _messagesIn;   // Phone → DAW
    private int _messagesOut;  // DAW → Phone

    /// <summary>Total MIDI messages routed from phone to DAW since bridge started.</summary>
    public int MessagesIn  => _messagesIn;

    /// <summary>Total MIDI messages routed from DAW to phone since bridge started.</summary>
    public int MessagesOut => _messagesOut;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired on every routed MIDI message. Subscribe in the ViewModel to populate
    /// the MIDI monitor log.
    /// </summary>
    public event Action<MidiMessage>? MidiMessageProcessed;

    public MidiBridge(
        TransportManager       transportManager,
        VirtualMidiPortService virtualPort,
        ILogger<MidiBridge>    logger)
    {
        _transportManager = transportManager;
        _virtualPort      = virtualPort;
        _logger           = logger;

        // Subscribe to all transport MIDI events upfront.
        // Only route bytes when the source is the current active transport.
        foreach (var (type, transport) in _transportManager.All)
            SubscribeTransport(type, transport);

        // DAW → Phone: route DAW clock / feedback back to active transport
        _virtualPort.DawMidiReceived += OnDawMidiReceived;
    }

    // ── Phone → DAW ───────────────────────────────────────────────────────────

    private void SubscribeTransport(TransportType type, IMidiTransport transport)
    {
        transport.MidiDataReceived += rawBytes =>
        {
            // Only route if this transport is the active one
            if (!ReferenceEquals(transport, _transportManager.ActiveTransport))
                return;

            // Forward to virtual port so the DAW receives the MIDI
            _virtualPort.SendMidi(rawBytes);

            Interlocked.Increment(ref _messagesIn);

            // Parse and fire for MIDI monitor
            var msg = MidiParser.Parse(rawBytes, MidiDirection.PhoneToDaw, type);
            if (msg is not null)
                MidiMessageProcessed?.Invoke(msg);
        };
    }

    // ── DAW → Phone ───────────────────────────────────────────────────────────

    private void OnDawMidiReceived(byte[] rawBytes)
    {
        var active = _transportManager.ActiveTransport;
        if (active is null) return;

        // Fire and forget — sending is async but we don't await here to avoid blocking callback
        _ = active.SendMidiAsync(rawBytes).ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogWarning(t.Exception, "Error sending DAW MIDI to {Name}", active.TransportName);
        }, TaskContinuationOptions.OnlyOnFaulted);

        Interlocked.Increment(ref _messagesOut);

        // Parse and fire for MIDI monitor (DAW→Phone direction)
        var msg = MidiParser.Parse(rawBytes, MidiDirection.DawToPhone, active.Type);
        if (msg is not null)
            MidiMessageProcessed?.Invoke(msg);
    }

    // ── Reset stats ───────────────────────────────────────────────────────────

    /// <summary>Reset the message counters to zero.</summary>
    public void ResetStats()
    {
        Interlocked.Exchange(ref _messagesIn,  0);
        Interlocked.Exchange(ref _messagesOut, 0);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _virtualPort.DawMidiReceived -= OnDawMidiReceived;
    }
}
