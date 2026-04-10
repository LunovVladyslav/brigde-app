using GearBoardBridge.Models;
using Microsoft.Extensions.Logging;

namespace GearBoardBridge.Services;

/// <summary>
/// Orchestrates all three MIDI transports (USB, WiFi, BLE).
/// Applies priority logic (USB > WiFi > BLE), starts/stops discovery
/// on all transports simultaneously, and fires <see cref="BetterTransportAvailable"/>
/// when a higher-priority transport becomes available while connected to a lower one.
/// </summary>
public sealed class TransportManager : IDisposable
{
    // TransportType enum values map directly to priority: USB=0, WiFi=1, BLE=2 (lower = better)
    private static int Priority(TransportType t) => (int)t;

    private readonly IReadOnlyDictionary<TransportType, IMidiTransport> _transports;
    private readonly ILogger<TransportManager> _logger;

    // Transports that currently have at least one discovered device ready to connect
    private readonly HashSet<TransportType> _availableTransports = [];
    private readonly Lock _lock = new();

    private IMidiTransport? _activeTransport;

    /// <summary>The transport currently used for the active MIDI connection.</summary>
    public IMidiTransport? ActiveTransport
    {
        get => _activeTransport;
        private set
        {
            _activeTransport = value;
            ActiveTransportChanged?.Invoke(value);
        }
    }

    /// <summary>All registered transports, keyed by type.</summary>
    public IReadOnlyDictionary<TransportType, IMidiTransport> All => _transports;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when the active transport changes (including when set to null on disconnect).
    /// </summary>
    public event Action<IMidiTransport?>? ActiveTransportChanged;

    /// <summary>
    /// Fired when a higher-priority transport becomes available while already connected
    /// to a lower-priority one.  Carries the suggested transport so the UI can show a banner.
    /// </summary>
    public event Action<IMidiTransport>? BetterTransportAvailable;

    /// <summary>Fired when any transport fires <see cref="IMidiTransport.DeviceDiscovered"/>.</summary>
    public event Action<DeviceInfo>? DeviceDiscovered;

    public TransportManager(
        BleMidiService   bleMidi,
        UsbMidiService   usbMidi,
        WifiMidiService  wifiMidi,
        ILogger<TransportManager> logger)
    {
        _logger = logger;

        _transports = new Dictionary<TransportType, IMidiTransport>
        {
            [TransportType.USB]  = usbMidi,
            [TransportType.WiFi] = wifiMidi,
            [TransportType.BLE]  = bleMidi,
        };

        foreach (var (type, transport) in _transports)
            Subscribe(type, transport);
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>Start discovery on all transports in parallel.</summary>
    public async Task StartAllDiscoveryAsync()
    {
        await Task.WhenAll(_transports.Values.Select(async t =>
        {
            try   { await t.StartDiscoveryAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discovery failed to start on {Name}", t.TransportName);
            }
        }));
    }

    /// <summary>Stop discovery on all transports in parallel.</summary>
    public async Task StopAllDiscoveryAsync()
    {
        await Task.WhenAll(_transports.Values.Select(async t =>
        {
            try   { await t.StopDiscoveryAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discovery failed to stop on {Name}", t.TransportName);
            }
        }));
    }

    // ── Connect / Disconnect ──────────────────────────────────────────────────

    /// <summary>
    /// Connect to the given device using the matching transport.
    /// Sets <see cref="ActiveTransport"/> on success.
    /// </summary>
    public async Task<bool> ConnectAsync(DeviceInfo device)
    {
        if (!_transports.TryGetValue(device.Transport, out var transport))
        {
            _logger.LogWarning("No transport registered for {Type}", device.Transport);
            return false;
        }

        var ok = await transport.ConnectAsync(device);
        if (ok)
            ActiveTransport = transport;
        return ok;
    }

    /// <summary>Disconnect the active transport and clear <see cref="ActiveTransport"/>.</summary>
    public async Task DisconnectAsync()
    {
        var active = ActiveTransport;
        if (active is null) return;

        try   { await active.DisconnectAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Disconnect error"); }

        ActiveTransport = null;
    }

    /// <summary>
    /// Switch the active transport to <paramref name="newTransport"/> without
    /// disconnecting from the device (caller is responsible for connecting first).
    /// Used when the user accepts a "better connection available" suggestion.
    /// </summary>
    public void SwitchActiveTransport(IMidiTransport newTransport)
    {
        ActiveTransport = newTransport;
        _logger.LogInformation("Active transport switched to {Name}", newTransport.TransportName);
    }

    // ── Internal subscription ─────────────────────────────────────────────────

    private void Subscribe(TransportType type, IMidiTransport transport)
    {
        transport.DeviceDiscovered += device =>
        {
            bool isNew;
            lock (_lock)
                isNew = _availableTransports.Add(type);

            // Bubble up for the ViewModel's device list
            DeviceDiscovered?.Invoke(device);

            if (!isNew) return;

            // Check if this transport is higher-priority than the active one
            var active = ActiveTransport;
            if (active is not null && Priority(type) < Priority(active.Type))
            {
                _logger.LogInformation(
                    "Higher-priority transport {Better} available while connected via {Current}",
                    type, active.Type);
                BetterTransportAvailable?.Invoke(transport);
            }
        };

        transport.StateChanged += state =>
        {
            if (state is ConnectionState.Idle or ConnectionState.Error)
            {
                lock (_lock)
                    _availableTransports.Remove(type);

                // If the active transport disconnected, clear it
                var active = ActiveTransport;
                if (active is not null && active.Type == type &&
                    state is ConnectionState.Error)
                {
                    _logger.LogWarning("Active transport {Name} entered error state", transport.TransportName);
                    // Leave ActiveTransport set so the ViewModel can still read the error;
                    // the ViewModel should call DisconnectAsync to fully clear.
                }
            }
        };
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var t in _transports.Values)
        {
            try { t.Dispose(); }
            catch { /* best-effort */ }
        }
    }
}
