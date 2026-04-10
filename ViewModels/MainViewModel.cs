using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GearBoardBridge.Models;
using GearBoardBridge.Services;
using Microsoft.Extensions.Logging;

namespace GearBoardBridge.ViewModels;

/// <summary>
/// Main application ViewModel.
/// Uses TransportManager (Phase 8) for all transport orchestration and
/// MidiBridge (Phase 8) for MIDI routing, augmented with SettingsService,
/// auto-connect, error handling, and BPM detection (Phase 9).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly SystemDetector        _systemDetector;
    private readonly TransportManager      _transportManager;
    private readonly MidiBridge            _midiBridge;
    private readonly SettingsService       _settings;
    private readonly ILogger<MainViewModel> _logger;

    // BPM detection — sliding window of MIDI clock ticks
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

    // ── Better connection suggestion (Phase 8) ────────────────────────────────

    [ObservableProperty] private bool _showSuggestionBanner;
    [ObservableProperty] private string? _suggestionText;
    private IMidiTransport? _suggestedTransport;

    // ── Error Handling (Phase 9) ──────────────────────────────────────────────

    [ObservableProperty] private bool _showBluetoothError;
    [ObservableProperty] private bool _showUsbModeError;
    [ObservableProperty] private bool _showWifiError;
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
        TransportManager       transportManager,
        MidiBridge             midiBridge,
        SettingsService        settings,
        ILogger<MainViewModel> logger)
    {
        _systemDetector   = systemDetector;
        _transportManager = transportManager;
        _midiBridge       = midiBridge;
        _settings         = settings;
        _logger           = logger;

        // ── Device discovery from any transport ───────────────────────────────
        _transportManager.DeviceDiscovered += device =>
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                if (!DiscoveredDevices.Any(d => d.Id == device.Id))
                    DiscoveredDevices.Add(device);

                TryAutoConnect(device);
            });
        };

        // ── Active transport changes ──────────────────────────────────────────
        _transportManager.ActiveTransportChanged += OnActiveTransportChanged;

        // ── Better connection suggestion (Phase 8) ────────────────────────────
        _transportManager.BetterTransportAvailable += better =>
        {
            _suggestedTransport = better;
            App.Current?.Dispatcher.Invoke(() =>
            {
                SuggestionText       = $"{better.TransportName} detected! Switch for lower latency?";
                ShowSuggestionBanner = true;
            });
        };

        // ── MIDI monitor feed (Phase 8) ───────────────────────────────────────
        _midiBridge.MidiMessageProcessed += msg =>
        {
            App.Current?.Dispatcher.Invoke(() => AddMidiMessage(msg));
        };
    }

    // ── Active transport state sync ───────────────────────────────────────────

    private void OnActiveTransportChanged(IMidiTransport? transport)
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            if (transport is null)
            {
                ConnectionState     = ConnectionState.Idle;
                ConnectedDeviceName = null;
                StatusText          = "Not connected";
                StatusIcon          = "○";
                StatusDotColor      = "#6B6B80";
                TransportBadge      = "";
                LatencyMs           = 0;
                return;
            }

            transport.StateChanged += state =>
            {
                if (!ReferenceEquals(transport, _transportManager.ActiveTransport)) return;

                App.Current?.Dispatcher.Invoke(() =>
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
        });
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

    // ── Auto-connect (Phase 9) ────────────────────────────────────────────────

    /// <summary>Called from App.xaml.cs after setup — starts scan if auto-connect is on.</summary>
    public async Task TryAutoConnectOnStartupAsync()
    {
        if (!_settings.Current.AutoConnectOnStartup) return;
        if (_settings.Current.LastDeviceName is null) return;

        _logger.LogInformation("Auto-connect: scanning for {Device}", _settings.Current.LastDeviceName);
        StatusText = $"Looking for {_settings.Current.LastDeviceName}...";
        await StartScanAsync();
    }

    private void TryAutoConnect(DeviceInfo device)
    {
        if (!_settings.Current.AutoConnectOnStartup) return;
        if (ConnectionState == ConnectionState.Connected) return;
        if (_settings.Current.LastDeviceName is null) return;

        bool nameMatch = device.Name == _settings.Current.LastDeviceName;
        bool transportMatch = _settings.Current.LastTransportType is null
                           || device.Transport == _settings.Current.LastTransportType;

        if (nameMatch && transportMatch)
        {
            _logger.LogInformation("Auto-connect: found {Name}, connecting...", device.Name);
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

            ShowBluetoothError = SystemCheck.HasBluetoothAdapter && !SystemCheck.IsBluetoothEnabled;
            ShowUsbModeError   = false;
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

        await _transportManager.StartAllDiscoveryAsync();

        if (!IsScanning) return;

        _ = Task.Delay(15_000).ContinueWith(async _ =>
        {
            if (IsScanning) await StopScanAsync();
        });
    }

    [RelayCommand]
    private async Task StopScanAsync()
    {
        await _transportManager.StopAllDiscoveryAsync();
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
        if (device is null) return;

        StatusText          = $"Connecting to {device.Name}...";
        StatusIcon          = "◐";
        StatusDotColor      = "#FFA726";
        ActiveTransportType = device.Transport;
        ErrorMessage        = null;

        var ok = await _transportManager.ConnectAsync(device);

        if (ok)
        {
            ConnectedDeviceName  = device.Name;
            StatusText           = $"Connected — {device.Name}";
            StatusIcon           = "●";
            StatusDotColor       = "#4CAF50";
            TransportBadge       = device.TransportIcon;
            LatencyMs            = _transportManager.ActiveTransport?.EstimatedLatencyMs ?? 0;
            ConnectionState      = ConnectionState.Connected;
            ErrorMessage         = null;

            _settings.SaveLastDevice(device.Name, device.Id, device.Transport);
        }
        else
        {
            StatusText     = $"Failed to connect to {device.Name}";
            StatusDotColor = "#EF5350";
            ErrorMessage   = GetTransportErrorHint(device.Transport);
            ShowBluetoothError = device.Transport == TransportType.BLE;
            ShowUsbModeError   = device.Transport == TransportType.USB;
            ShowWifiError      = device.Transport == TransportType.WiFi;
        }
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        await _transportManager.DisconnectAsync();
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

    // ── Suggestion banner commands (Phase 8) ──────────────────────────────────

    [RelayCommand]
    private async Task AcceptSuggestionAsync()
    {
        ShowSuggestionBanner = false;
        var suggested = _suggestedTransport;
        if (suggested is null) return;

        await _transportManager.DisconnectAsync();
        var device = suggested.DiscoveredDevices.FirstOrDefault();
        if (device is not null)
            await ConnectToDeviceAsync(device);

        _suggestedTransport = null;
    }

    [RelayCommand]
    private void DismissSuggestion()
    {
        ShowSuggestionBanner = false;
        _suggestedTransport  = null;
    }

    // ── Error banner commands (Phase 9) ──────────────────────────────────────

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
                FileName = "ms-settings:bluetooth", UseShellExecute = true
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
                FileName = "ms-settings:network-wifi", UseShellExecute = true
            });
        }
        catch { }
    }

    // ── MIDI Monitor ─────────────────────────────────────────────────────────

    public void AddMidiMessage(MidiMessage message)
    {
        MidiLog.Insert(0, message);
        if (MidiLog.Count > 200)
            MidiLog.RemoveAt(MidiLog.Count - 1);
        MessageCount++;

        if (message.Type == MidiMessageType.Clock)
            UpdateBpm();
    }

    private void UpdateBpm()
    {
        var now = DateTime.UtcNow;
        _clockTicks.Enqueue(now);
        while (_clockTicks.Count > 24)
            _clockTicks.Dequeue();

        if (_clockTicks.Count < 4) return;

        var ticks     = _clockTicks.ToArray();
        var totalSpan = (ticks[^1] - ticks[0]).TotalSeconds;
        if (totalSpan <= 0) return;

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
