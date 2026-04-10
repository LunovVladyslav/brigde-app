using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly SystemDetector        _systemDetector;
    private readonly TransportManager      _transportManager;
    private readonly MidiBridge            _midiBridge;
    private readonly ILogger<MainViewModel> _logger;

    // ── Connection State ──
    [ObservableProperty] private ConnectionState _connectionState = ConnectionState.Idle;
    [ObservableProperty] private string _statusText = "Not connected";
    [ObservableProperty] private string _statusIcon = "○";
    [ObservableProperty] private string? _connectedDeviceName;
    [ObservableProperty] private TransportType _activeTransportType = TransportType.BLE;
    [ObservableProperty] private string _transportBadge = "";
    [ObservableProperty] private double _latencyMs;

    // ── Scanning ──
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private ObservableCollection<DeviceInfo> _discoveredDevices = [];

    // ── System Check ──
    [ObservableProperty] private SystemCheckResult? _systemCheck;
    [ObservableProperty] private bool _isCheckingSystem;
    [ObservableProperty] private bool _showSetupWizard = true;

    // ── MIDI Monitor ──
    [ObservableProperty] private ObservableCollection<MidiMessage> _midiLog = [];
    [ObservableProperty] private int _messageCount;
    [ObservableProperty] private float _detectedBpm;
    [ObservableProperty] private bool _isMonitorExpanded = true;

    // ── Transport Availability ──
    [ObservableProperty] private bool _usbAvailable;
    [ObservableProperty] private bool _wifiAvailable;
    [ObservableProperty] private bool _bleAvailable;

    // ── Better connection suggestion ──
    [ObservableProperty] private bool _showSuggestionBanner;
    [ObservableProperty] private string? _suggestionText;
    private IMidiTransport? _suggestedTransport;

    // ── Visual State ──
    /// <summary>Hex color for the status dot — gray/amber/green/red per connection state.</summary>
    [ObservableProperty] private string _statusDotColor = "#6B6B80";

    /// <summary>Hex color for the USB transport pill — green when active, neutral otherwise.</summary>
    public string UsbPillColor  => IsActiveTransport(TransportType.USB)  ? "#2E7D32" : "#2D2D4A";
    /// <summary>Hex color for the WiFi transport pill — green when active, neutral otherwise.</summary>
    public string WifiPillColor => IsActiveTransport(TransportType.WiFi) ? "#2E7D32" : "#2D2D4A";
    /// <summary>Hex color for the BLE transport pill — green when active, neutral otherwise.</summary>
    public string BlePillColor  => IsActiveTransport(TransportType.BLE)  ? "#2E7D32" : "#2D2D4A";

    private bool IsActiveTransport(TransportType t)
        => ConnectionState == ConnectionState.Connected && ActiveTransportType == t;

    public MainViewModel(
        SystemDetector         systemDetector,
        TransportManager       transportManager,
        MidiBridge             midiBridge,
        ILogger<MainViewModel> logger)
    {
        _systemDetector   = systemDetector;
        _transportManager = transportManager;
        _midiBridge       = midiBridge;
        _logger           = logger;

        // ── Device discovery from any transport ──
        _transportManager.DeviceDiscovered += device =>
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                if (!DiscoveredDevices.Any(d => d.Id == device.Id))
                    DiscoveredDevices.Add(device);
            });
        };

        // ── Active transport switch notification ──
        _transportManager.ActiveTransportChanged += OnActiveTransportChanged;

        // ── Better connection suggestion banner ──
        _transportManager.BetterTransportAvailable += better =>
        {
            _suggestedTransport = better;
            App.Current?.Dispatcher.Invoke(() =>
            {
                SuggestionText       = $"{better.TransportName} detected! Switch for lower latency?";
                ShowSuggestionBanner = true;
            });
        };

        // ── MIDI monitor ──
        _midiBridge.MidiMessageProcessed += msg =>
        {
            App.Current?.Dispatcher.Invoke(() => AddMidiMessage(msg));
        };
    }

    // ── Transport state sync ──────────────────────────────────────────────────

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

            // Subscribe to state changes of this specific transport
            transport.StateChanged += state =>
            {
                // Ignore if this is no longer the active transport
                if (!ReferenceEquals(transport, _transportManager.ActiveTransport)) return;

                App.Current?.Dispatcher.Invoke(() =>
                {
                    ConnectionState = state;
                    if (state == ConnectionState.Error)
                    {
                        StatusText          = "Error: Connection lost";
                        StatusDotColor      = "#EF5350";
                        ConnectedDeviceName = null;
                        TransportBadge      = "";
                        LatencyMs           = 0;
                    }
                    else if (state == ConnectionState.Reconnecting)
                    {
                        StatusText     = $"Reconnecting to {ConnectedDeviceName}...";
                        StatusDotColor = "#FFA726";
                    }
                });
            };
        });
    }

    // Notify pill colors when connection state or active transport type changes
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

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Run system detection on startup.</summary>
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

    /// <summary>Skip setup wizard (user already has everything configured).</summary>
    [RelayCommand]
    private void SkipSetup() => ShowSetupWizard = false;

    /// <summary>Start scanning for GearBoard devices across all transports.</summary>
    [RelayCommand]
    private async Task StartScanAsync()
    {
        IsScanning = true;
        DiscoveredDevices.Clear();
        StatusText     = "Scanning for GearBoard...";
        StatusIcon     = "◐";
        StatusDotColor = "#FFA726";

        await _transportManager.StartAllDiscoveryAsync();

        if (!IsScanning) return; // stopped externally

        // Auto-stop after 15 seconds
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

    /// <summary>Connect to a discovered device using the matching transport.</summary>
    [RelayCommand]
    private async Task ConnectToDeviceAsync(DeviceInfo? device)
    {
        if (device is null) return;

        StatusText          = $"Connecting to {device.Name}...";
        StatusIcon          = "◐";
        StatusDotColor      = "#FFA726";
        ActiveTransportType = device.Transport;

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
        }
        else
        {
            StatusText     = $"Failed to connect to {device.Name}";
            StatusDotColor = "#EF5350";
        }
    }

    /// <summary>Disconnect from the active transport.</summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _transportManager.DisconnectAsync();
        // UI state is reset via OnActiveTransportChanged(null)
    }

    /// <summary>Accept the suggested higher-priority transport.</summary>
    [RelayCommand]
    private async Task AcceptSuggestionAsync()
    {
        ShowSuggestionBanner = false;
        var suggested = _suggestedTransport;
        if (suggested is null) return;

        // Disconnect current, then connect the suggested device (first discovered)
        await _transportManager.DisconnectAsync();
        var device = suggested.DiscoveredDevices.FirstOrDefault();
        if (device is not null)
            await ConnectToDeviceAsync(device);

        _suggestedTransport = null;
    }

    /// <summary>Dismiss the "better transport available" suggestion.</summary>
    [RelayCommand]
    private void DismissSuggestion()
    {
        ShowSuggestionBanner = false;
        _suggestedTransport  = null;
    }

    // ── MIDI Monitor ──────────────────────────────────────────────────────────

    /// <summary>Add a MIDI message to the monitor log (max 200 entries).</summary>
    public void AddMidiMessage(MidiMessage message)
    {
        MidiLog.Insert(0, message);
        if (MidiLog.Count > 200)
            MidiLog.RemoveAt(MidiLog.Count - 1);
        MessageCount++;
        _midiBridge.ResetStats(); // keeps stats in sync with displayed count
    }

    [RelayCommand]
    private void ClearMidiLog()
    {
        MidiLog.Clear();
        MessageCount = 0;
    }

    [RelayCommand]
    private void ToggleMonitor() => IsMonitorExpanded = !IsMonitorExpanded;

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
}
