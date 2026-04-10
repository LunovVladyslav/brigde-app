using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GearBoardBridge.Helpers;
using GearBoardBridge.Models;
using GearBoardBridge.Services;
using Microsoft.Extensions.Logging;

namespace GearBoardBridge.ViewModels;

/// <summary>
/// Main application ViewModel — manages transport connections,
/// device scanning, MIDI monitoring, and system state.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly SystemDetector               _systemDetector;
    private readonly IReadOnlyList<IMidiTransport> _transports;
    private readonly VirtualMidiPortService        _virtualMidiPort;
    private readonly SettingsService               _settings;
    private readonly ILogger<MainViewModel>        _logger;

    private IMidiTransport? _activeTransport;

    // BPM detection — track MIDI clock tick timestamps
    private readonly Queue<DateTime> _clockTicks = new();

    // ── Connection State ──────────────────────────────────────────────────────

    [ObservableProperty] private ConnectionState _connectionState = ConnectionState.Idle;
    [ObservableProperty] private string _statusText = "Not connected";
    [ObservableProperty] private string _statusIcon = "○";
    [ObservableProperty] private string? _connectedDeviceName;
    [ObservableProperty] private TransportType _activeTransportType = TransportType.BLE;
    [ObservableProperty] private string _transportBadge = "";
    [ObservableProperty] private double _latencyMs;

    // ── Scanning ─────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private ObservableCollection<DeviceInfo> _discoveredDevices = [];

    // ── System Check ─────────────────────────────────────────────────────────

    [ObservableProperty] private SystemCheckResult? _systemCheck;
    [ObservableProperty] private bool _isCheckingSystem;
    [ObservableProperty] private bool _showSetupWizard = true;

    // ── MIDI Monitor ─────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<MidiMessage> _midiLog = [];
    [ObservableProperty] private int _messageCount;
    [ObservableProperty] private float _detectedBpm;
    [ObservableProperty] private bool _isMonitorExpanded = true;

    // ── Transport Availability ────────────────────────────────────────────────

    [ObservableProperty] private bool _usbAvailable;
    [ObservableProperty] private bool _wifiAvailable;
    [ObservableProperty] private bool _bleAvailable;
    [ObservableProperty] private string? _suggestionText;

    // ── Error Handling ────────────────────────────────────────────────────────

    /// <summary>True when Bluetooth adapter is off — shows the "Enable Bluetooth" banner.</summary>
    [ObservableProperty] private bool _showBluetoothError;

    /// <summary>True when no USB MIDI mode detected — shows USB setup instructions.</summary>
    [ObservableProperty] private bool _showUsbModeError;

    /// <summary>True when WiFi transport fails — shows network troubleshooting.</summary>
    [ObservableProperty] private bool _showWifiError;

    /// <summary>Generic connection error message shown under the status card.</summary>
    [ObservableProperty] private string? _errorMessage;

    // ── Visual State ─────────────────────────────────────────────────────────

    [ObservableProperty] private string _statusDotColor = "#6B6B80";

    public string UsbPillColor  => IsActiveTransport(TransportType.USB)  ? "#2E7D32" : "#2D2D4A";
    public string WifiPillColor => IsActiveTransport(TransportType.WiFi) ? "#2E7D32" : "#2D2D4A";
    public string BlePillColor  => IsActiveTransport(TransportType.BLE)  ? "#2E7D32" : "#2D2D4A";

    private bool IsActiveTransport(TransportType t)
        => ConnectionState == ConnectionState.Connected && ActiveTransportType == t;

    public MainViewModel(
        SystemDetector         systemDetector,
        BleMidiService         bleMidi,
        UsbMidiService         usbMidi,
        WifiMidiService        wifiMidi,
        VirtualMidiPortService virtualMidiPort,
        SettingsService        settings,
        ILogger<MainViewModel> logger)
    {
        _systemDetector  = systemDetector;
        _virtualMidiPort = virtualMidiPort;
        _settings        = settings;
        _logger          = logger;

        _transports = [bleMidi, usbMidi, wifiMidi];

        foreach (var transport in _transports)
            SubscribeTransport(transport);

        _virtualMidiPort.DawMidiReceived += bytes =>
        {
            var active = _activeTransport;
            if (active != null)
                _ = active.SendMidiAsync(bytes);
        };
    }

    // ── Transport subscriptions ───────────────────────────────────────────────

    private void SubscribeTransport(IMidiTransport transport)
    {
        transport.DeviceDiscovered += device =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (!DiscoveredDevices.Any(d => d.Id == device.Id))
                    DiscoveredDevices.Add(device);

                // Auto-connect: if this is the saved device, connect immediately
                TryAutoConnect(device);
            });
        };

        transport.MidiDataReceived += rawBytes =>
        {
            _virtualMidiPort.SendMidi(rawBytes);
            var msg = MidiParser.Parse(rawBytes, MidiDirection.PhoneToDaw, transport.Type);
            if (msg != null) AddMidiMessage(msg);
        };

        transport.StateChanged += state =>
        {
            if (!ReferenceEquals(transport, _activeTransport)) return;

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                ConnectionState = state;
                switch (state)
                {
                    case ConnectionState.Error:
                        StatusText          = "Connection lost";
                        StatusDotColor      = "#EF5350";
                        ConnectedDeviceName = null;
                        TransportBadge      = "";
                        LatencyMs           = 0;
                        ErrorMessage        = GetTransportErrorHint(transport.Type);
                        break;

                    case ConnectionState.Reconnecting:
                        StatusText     = $"Reconnecting to {ConnectedDeviceName}...";
                        StatusDotColor = "#FFA726";
                        ErrorMessage   = null;
                        break;

                    case ConnectionState.Connected:
                        ErrorMessage = null;
                        break;
                }
            });
        };
    }

    private static string GetTransportErrorHint(TransportType type) => type switch
    {
        TransportType.BLE  => "Make sure GearBoard is running and Bluetooth is enabled.",
        TransportType.USB  => "Check the USB cable and ensure the phone is in MIDI mode.",
        TransportType.WiFi => "Ensure both devices are on the same Wi-Fi network.",
        _                  => "Check the connection and try again."
    };

    // ── Pill color reactivity ─────────────────────────────────────────────────

    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(UsbPillColor));
        OnPropertyChanged(nameof(WifiPillColor));
        OnPropertyChanged(nameof(BlePillColor));
    }

    partial void OnActiveTransportTypeChanged(TransportType value)
    {
        OnPropertyChanged(nameof(UsbPillColor));
        OnPropertyChanged(nameof(WifiPillColor));
        OnPropertyChanged(nameof(BlePillColor));
    }

    // ── Auto-connect ──────────────────────────────────────────────────────────

    /// <summary>
    /// Called from App.xaml.cs after setup wizard completes.
    /// If auto-connect is enabled and we have a saved device, starts scanning.
    /// </summary>
    public async Task TryAutoConnectOnStartupAsync()
    {
        if (!_settings.Current.AutoConnectOnStartup) return;
        if (_settings.Current.LastDeviceName is null)  return;

        _logger.LogInformation("Auto-connect: scanning for {Device}", _settings.Current.LastDeviceName);
        StatusText = $"Looking for {_settings.Current.LastDeviceName}...";
        await StartScanAsync();
    }

    private void TryAutoConnect(DeviceInfo device)
    {
        if (!_settings.Current.AutoConnectOnStartup) return;
        if (ConnectionState == ConnectionState.Connected) return;
        if (_settings.Current.LastDeviceName is null) return;

        // Match by name (and transport if available)
        bool nameMatch = device.Name == _settings.Current.LastDeviceName;
        bool transportMatch = _settings.Current.LastTransportType is null
                           || device.Transport == _settings.Current.LastTransportType;

        if (nameMatch && transportMatch)
        {
            _logger.LogInformation("Auto-connect: found saved device {Name}, connecting...", device.Name);
            _ = ConnectToDeviceAsync(device);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RunSystemCheckAsync()
    {
        IsCheckingSystem = true;
        try
        {
            SystemCheck   = await _systemDetector.DetectAsync();
            BleAvailable  = SystemCheck.HasBleSupport;
            UsbAvailable  = SystemCheck.HasUsbMidiDevice;
            WifiAvailable = SystemCheck.HasWifiConnection;

            // Show error banners for missing capabilities
            ShowBluetoothError = SystemCheck.HasBluetoothAdapter && !SystemCheck.IsBluetoothEnabled;
            ShowUsbModeError   = false; // cleared until USB connection attempt fails
            ShowWifiError      = false;

            if (SystemCheck.AllRequirementsMet)
                ShowSetupWizard = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "System check failed");
        }
        finally
        {
            IsCheckingSystem = false;
        }
    }

    [RelayCommand]
    private void SkipSetup() => ShowSetupWizard = false;

    [RelayCommand]
    private async Task StartScanAsync()
    {
        IsScanning = true;
        DiscoveredDevices.Clear();
        StatusText     = "Scanning for GearBoard...";
        StatusIcon     = "◐";
        StatusDotColor = "#FFA726";

        var startResults = await Task.WhenAll(_transports.Select(async t =>
        {
            try   { return await t.StartDiscoveryAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transport {Name} scan failed to start", t.TransportName);
                return false;
            }
        }));

        if (!startResults.Any(r => r))
        {
            IsScanning      = false;
            StatusText      = "All transport scans failed. Check Bluetooth and network.";
            StatusDotColor  = "#EF5350";
            ConnectionState = ConnectionState.Idle;
            return;
        }

        _ = Task.Delay(15_000).ContinueWith(async _ =>
        {
            if (IsScanning) await StopScanAsync();
        });
    }

    [RelayCommand]
    private async Task StopScanAsync()
    {
        await Task.WhenAll(_transports.Select(async t =>
        {
            try   { await t.StopDiscoveryAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Stop scan failed for {Name}", t.TransportName); }
        }));

        IsScanning     = false;
        StatusDotColor = "#6B6B80";
        if (ConnectionState == ConnectionState.Scanning)
        {
            ConnectionState = ConnectionState.Idle;
            StatusText = DiscoveredDevices.Count > 0
                ? $"{DiscoveredDevices.Count} device(s) found"
                : "No devices found. Make sure GearBoard is running.";
        }
    }

    [RelayCommand]
    private async Task ConnectToDeviceAsync(DeviceInfo? device)
    {
        if (device == null) return;

        StatusText          = $"Connecting to {device.Name}...";
        StatusIcon          = "◐";
        StatusDotColor      = "#FFA726";
        ActiveTransportType = device.Transport;
        ErrorMessage        = null;

        _activeTransport = _transports.FirstOrDefault(t => t.Type == device.Transport);
        if (_activeTransport == null)
        {
            StatusText     = $"No handler for transport {device.Transport}";
            StatusDotColor = "#EF5350";
            return;
        }

        var ok = await _activeTransport.ConnectAsync(device);

        if (ok)
        {
            ConnectedDeviceName = device.Name;
            StatusText          = $"Connected — {device.Name}";
            StatusIcon          = "●";
            StatusDotColor      = "#4CAF50";
            TransportBadge      = device.TransportIcon;
            LatencyMs           = _activeTransport.EstimatedLatencyMs;
            ConnectionState     = ConnectionState.Connected;
            ErrorMessage        = null;

            // Persist for auto-connect next launch
            _settings.SaveLastDevice(device.Name, device.Id, device.Transport);
        }
        else
        {
            StatusText     = $"Failed to connect to {device.Name}";
            StatusDotColor = "#EF5350";
            ErrorMessage   = GetTransportErrorHint(device.Transport);
            ShowTransportErrorBanner(device.Transport);
            _activeTransport = null;
        }
    }

    private void ShowTransportErrorBanner(TransportType type)
    {
        ShowBluetoothError = type == TransportType.BLE;
        ShowUsbModeError   = type == TransportType.USB;
        ShowWifiError      = type == TransportType.WiFi;
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await (_activeTransport?.DisconnectAsync() ?? Task.CompletedTask);
        _activeTransport    = null;
        ConnectedDeviceName = null;
        StatusText          = "Not connected";
        StatusIcon          = "○";
        StatusDotColor      = "#6B6B80";
        TransportBadge      = "";
        LatencyMs           = 0;
        ErrorMessage        = null;
        ShowBluetoothError  = false;
        ShowUsbModeError    = false;
        ShowWifiError       = false;
    }

    // ── Error banner dismiss / actions ────────────────────────────────────────

    [RelayCommand]
    private void DismissBluetoothError() => ShowBluetoothError = false;

    [RelayCommand]
    private void DismissUsbError() => ShowUsbModeError = false;

    [RelayCommand]
    private void DismissWifiError() => ShowWifiError = false;

    [RelayCommand]
    private void OpenBluetoothSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "ms-settings:bluetooth",
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    private void OpenWifiSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "ms-settings:network-wifi",
                UseShellExecute = true
            });
        }
        catch { }
    }

    // ── MIDI Monitor ─────────────────────────────────────────────────────────

    public void AddMidiMessage(MidiMessage message)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            MidiLog.Insert(0, message);
            if (MidiLog.Count > 200)
                MidiLog.RemoveAt(MidiLog.Count - 1);
            MessageCount++;

            if (message.Type == MidiMessageType.Clock)
                UpdateBpm();
        });
    }

    private void UpdateBpm()
    {
        var now = DateTime.UtcNow;
        _clockTicks.Enqueue(now);

        // Keep only the last 24 ticks (one bar at 4/4)
        while (_clockTicks.Count > 24)
            _clockTicks.Dequeue();

        if (_clockTicks.Count < 4) return;

        var ticks     = _clockTicks.ToArray();
        var totalSpan = (ticks[^1] - ticks[0]).TotalSeconds;
        if (totalSpan <= 0) return;

        // MIDI clock = 24 ticks per quarter note
        var ticksPerSecond = (ticks.Length - 1) / totalSpan;
        DetectedBpm = (float)(ticksPerSecond / 24.0 * 60.0);
    }

    [RelayCommand]
    private void ClearMidiLog()
    {
        MidiLog.Clear();
        MessageCount = 0;
        DetectedBpm  = 0;
        _clockTicks.Clear();
    }

    [RelayCommand]
    private void ToggleMonitor() => IsMonitorExpanded = !IsMonitorExpanded;
}
