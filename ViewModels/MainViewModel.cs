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
    private readonly SystemDetector _systemDetector;
    private readonly BleMidiService _bleMidi;
    private readonly VirtualMidiPortService _virtualMidiPort;
    private readonly ILogger<MainViewModel> _logger;

    // ── Connection State ──
    [ObservableProperty] private ConnectionState _connectionState = ConnectionState.Idle;
    [ObservableProperty] private string _statusText = "Not connected";
    [ObservableProperty] private string _statusIcon = "○";
    [ObservableProperty] private string? _connectedDeviceName;
    [ObservableProperty] private TransportType _activeTransport = TransportType.BLE;
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
    [ObservableProperty] private string? _suggestionText;

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
        => ConnectionState == ConnectionState.Connected && ActiveTransport == t;

    public MainViewModel(SystemDetector systemDetector, BleMidiService bleMidi,
                         VirtualMidiPortService virtualMidiPort,
                         ILogger<MainViewModel> logger)
    {
        _systemDetector  = systemDetector;
        _bleMidi         = bleMidi;
        _virtualMidiPort = virtualMidiPort;
        _logger          = logger;

        // ── Subscribe to BLE MIDI events ──
        _bleMidi.DeviceDiscovered += device =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (!DiscoveredDevices.Any(d => d.Id == device.Id))
                    DiscoveredDevices.Add(device);
            });
        };

        _bleMidi.MidiDataReceived += rawBytes =>
        {
            // Forward to virtual MIDI port so DAW receives the data
            _virtualMidiPort.SendMidi(rawBytes);

            var msg = MidiParser.Parse(rawBytes, MidiDirection.PhoneToDaw, TransportType.BLE);
            if (msg != null) AddMidiMessage(msg);
        };

        _bleMidi.StateChanged += state =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                ConnectionState = state;
                if (state == ConnectionState.Error)
                {
                    StatusText     = "Error: Connection lost";
                    StatusDotColor = "#EF5350";
                    ConnectedDeviceName = null;
                    TransportBadge = "";
                    LatencyMs      = 0;
                }
            });
        };
    }

    // Notify pill colors when connection state or active transport changes
    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(UsbPillColor));
        OnPropertyChanged(nameof(WifiPillColor));
        OnPropertyChanged(nameof(BlePillColor));
    }

    partial void OnActiveTransportChanged(TransportType value)
    {
        OnPropertyChanged(nameof(UsbPillColor));
        OnPropertyChanged(nameof(WifiPillColor));
        OnPropertyChanged(nameof(BlePillColor));
    }

    /// <summary>
    /// Run system detection on startup.
    /// </summary>
    [RelayCommand]
    private async Task RunSystemCheckAsync()
    {
        IsCheckingSystem = true;
        try
        {
            SystemCheck = await _systemDetector.DetectAsync();
            BleAvailable = SystemCheck.HasBleSupport;
            UsbAvailable = SystemCheck.HasUsbMidiDevice;
            WifiAvailable = SystemCheck.HasWifiConnection;

            if (SystemCheck.AllRequirementsMet)
            {
                ShowSetupWizard = false;
            }
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

    /// <summary>
    /// Skip setup wizard (user already has everything configured).
    /// </summary>
    [RelayCommand]
    private void SkipSetup()
    {
        ShowSetupWizard = false;
    }

    /// <summary>
    /// Start BLE scan for GearBoard devices.
    /// </summary>
    [RelayCommand]
    private async Task StartScanAsync()
    {
        IsScanning = true;
        DiscoveredDevices.Clear();
        StatusText     = "Scanning for GearBoard...";
        StatusIcon     = "◐";
        StatusDotColor = "#FFA726"; // amber while scanning
        ActiveTransport = TransportType.BLE;

        var started = await _bleMidi.StartDiscoveryAsync();
        if (!started)
        {
            IsScanning     = false;
            StatusText     = "Bluetooth scan failed. Check BT is enabled.";
            StatusDotColor = "#EF5350";
            ConnectionState = ConnectionState.Idle;
        }

        // Auto-stop scan after 15 seconds if still running
        _ = Task.Delay(15_000).ContinueWith(async _ =>
        {
            if (IsScanning) await StopScanAsync();
        });
    }

    [RelayCommand]
    private async Task StopScanAsync()
    {
        await _bleMidi.StopDiscoveryAsync();
        IsScanning     = false;
        StatusDotColor = "#6B6B80";
        if (ConnectionState == ConnectionState.Scanning)
        {
            ConnectionState = ConnectionState.Idle;
            StatusText      = DiscoveredDevices.Count > 0
                ? $"{DiscoveredDevices.Count} device(s) found"
                : "No devices found. Make sure GearBoard is running.";
        }
    }

    /// <summary>
    /// Connect to a discovered BLE MIDI device.
    /// </summary>
    [RelayCommand]
    private async Task ConnectToDeviceAsync(DeviceInfo? device)
    {
        if (device == null) return;

        StatusText      = $"Connecting to {device.Name}...";
        StatusIcon      = "◐";
        StatusDotColor  = "#FFA726";
        ActiveTransport = device.Transport;

        var ok = await _bleMidi.ConnectAsync(device);

        if (ok)
        {
            ConnectedDeviceName = device.Name;
            StatusText     = $"Connected — {device.Name}";
            StatusIcon     = "●";
            StatusDotColor = "#4CAF50";
            TransportBadge = device.TransportIcon;
            LatencyMs      = _bleMidi.EstimatedLatencyMs;
        }
        else
        {
            StatusText     = $"Failed to connect to {device.Name}";
            StatusDotColor = "#EF5350";
        }
    }

    /// <summary>
    /// Disconnect from the current BLE device.
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _bleMidi.DisconnectAsync();
        ConnectedDeviceName = null;
        StatusText     = "Not connected";
        StatusIcon     = "○";
        StatusDotColor = "#6B6B80";
        TransportBadge = "";
        LatencyMs      = 0;
    }

    /// <summary>
    /// Add a MIDI message to the monitor log.
    /// </summary>
    public void AddMidiMessage(MidiMessage message)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            MidiLog.Insert(0, message);
            if (MidiLog.Count > 200)
                MidiLog.RemoveAt(MidiLog.Count - 1);
            MessageCount++;

            if (message.Type == MidiMessageType.Clock)
            {
                // BPM detection would go here
            }
        });
    }

    [RelayCommand]
    private void ClearMidiLog()
    {
        MidiLog.Clear();
        MessageCount = 0;
    }

    [RelayCommand]
    private void ToggleMonitor()
    {
        IsMonitorExpanded = !IsMonitorExpanded;
    }

    [RelayCommand]
    private void OpenBluetoothSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:bluetooth",
                UseShellExecute = true
            });
        }
        catch { }
    }
}
