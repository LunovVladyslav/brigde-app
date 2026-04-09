using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GearBoardBridge.Services;

/// <summary>
/// Creates and manages a virtual MIDI port that DAWs (Ableton, etc.) can see.
///
/// Strategy:
///   1. teVirtualMIDI (loopMIDI driver) — P/Invoke to teVirtualMIDI64.dll.
///      Requires loopMIDI to be installed. Works on all Windows versions.
///   2. Graceful no-op if neither is available — app still works for monitoring.
/// </summary>
public sealed class VirtualMidiPortService : IDisposable
{
    // ── teVirtualMIDI P/Invoke ──────────────────────────────────────────────
    // teVirtualMIDI64.dll is installed by loopMIDI into System32.
    // SDK: https://www.tobias-erichsen.de/software/virtualmidi/virtualmidi-sdk.html

    private const string DllName = "teVirtualMIDI64.dll";

    // Callback delegate — called when DAW sends MIDI into the port (DAW→GearBoard direction)
    private delegate void TeVirtualMidiCallback(
        IntPtr instance, IntPtr midiDataBytes, uint length, IntPtr dwCallbackInstance);

    [DllImport(DllName, EntryPoint = "virtualMIDICreatePortEx2",
               CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreatePortEx2(
        string portName,
        TeVirtualMidiCallback? callback,
        IntPtr dwCallbackInstance,
        uint maxSysexLength,
        uint flags);

    [DllImport(DllName, EntryPoint = "virtualMIDISendData", SetLastError = true)]
    private static extern bool SendData(IntPtr port, byte[] midiDataBytes, uint length);

    [DllImport(DllName, EntryPoint = "virtualMIDIClosePort")]
    private static extern void ClosePort(IntPtr port);

    // flags: TE_VM_FLAGS_SUPPORTED_FUNCTIONS = 3 (both send and receive)
    private const uint TE_VM_FLAGS_SUPPORTED_FUNCTIONS = 3;
    private const uint MaxSysexLength = 65535;

    // ── State ───────────────────────────────────────────────────────────────

    private IntPtr _portHandle = IntPtr.Zero;
    private TeVirtualMidiCallback? _callbackRef; // keep alive to prevent GC
    private readonly ILogger<VirtualMidiPortService> _logger;

    /// <summary>True when a virtual MIDI port has been successfully opened.</summary>
    public bool IsOpen => _portHandle != IntPtr.Zero;

    /// <summary>
    /// Fired when the DAW sends MIDI back into the port (e.g. clock sync, feedback).
    /// Payload is raw MIDI bytes.
    /// </summary>
    public event Action<byte[]>? DawMidiReceived;

    public VirtualMidiPortService(ILogger<VirtualMidiPortService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempt to open a virtual MIDI port with the given name.
    /// Returns true if the port was created successfully.
    /// </summary>
    public bool Open(string portName = "GearBoard MIDI")
    {
        if (IsOpen)
        {
            _logger.LogWarning("VirtualMidiPortService: port already open.");
            return true;
        }

        if (!IsDllAvailable())
        {
            _logger.LogWarning(
                "teVirtualMIDI64.dll not found — virtual MIDI port will not be created. " +
                "Install loopMIDI from https://www.tobias-erichsen.de/software/loopmidi.html");
            return false;
        }

        try
        {
            _callbackRef = OnDawMidiData;
            _portHandle = CreatePortEx2(
                portName,
                _callbackRef,
                IntPtr.Zero,
                MaxSysexLength,
                TE_VM_FLAGS_SUPPORTED_FUNCTIONS);

            if (_portHandle == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogError(
                    "Failed to create virtual MIDI port '{Name}'. Win32 error: {Error}",
                    portName, err);
                return false;
            }

            _logger.LogInformation(
                "Virtual MIDI port '{Name}' created — visible in Ableton and other DAWs.", portName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while creating virtual MIDI port '{Name}'", portName);
            _portHandle = IntPtr.Zero;
            return false;
        }
    }

    /// <summary>
    /// Send raw MIDI bytes from GearBoard → virtual port → DAW.
    /// </summary>
    public void SendMidi(byte[] data)
    {
        if (!IsOpen || data.Length == 0) return;

        try
        {
            var ok = SendData(_portHandle, data, (uint)data.Length);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning("SendMidi failed. Win32 error: {Error}", err);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in SendMidi");
        }
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Called by teVirtualMIDI when the DAW writes MIDI into our port.
    /// Fires DawMidiReceived for upstream consumers (e.g. clock sync back to GearBoard).
    /// </summary>
    private void OnDawMidiData(
        IntPtr instance, IntPtr midiDataBytes, uint length, IntPtr dwCallbackInstance)
    {
        if (length == 0 || midiDataBytes == IntPtr.Zero) return;

        var bytes = new byte[length];
        Marshal.Copy(midiDataBytes, bytes, 0, (int)length);
        DawMidiReceived?.Invoke(bytes);
    }

    private static bool IsDllAvailable()
    {
        try
        {
            // Attempt a trivial load — if it throws DllNotFoundException the driver isn't installed
            NativeLibrary.TryLoad(DllName, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_portHandle != IntPtr.Zero)
        {
            try { ClosePort(_portHandle); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error closing virtual MIDI port"); }
            _portHandle = IntPtr.Zero;
        }
    }
}
