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
    private readonly SystemDetector           _systemDetector;
    private readonly IReadOnlyList<IMidiTransport> _transports;
    private readonly VirtualMidiPortService   _virtualMidiPort;
    private readonly ILogger<MainViewModel>   _logger;

    // The transport currently used for the active connection (null when disconnected)
    private IMidiTransport? _activeTransport;

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
        => ConnectionState == ConnectionState.Connected && ActiveTransportType == t;

    public MainViewModel(SystemDetector systemDetector,
                         BleMidiService bleMidi,
                         UsbMidiService usbMidi,
                         WifiMidiService wifiMidi,
                         VirtualMidiPortService virtualMidiPort,
                         ILogger<MainViewModel> logger)
    {
        _systemDetector  = systemDetector;
        _virtualMidiPort = virtualMidiPort;
        _logger          = logger;

        _transports = [bleMidi, usbMidi, wifiMidi];

        // ── Subscribe to events from all transports ──
        foreach (var transport in _transports)
            SubscribeTransport(transport);

        // ── Forward DAW MIDI clock back to the active transport ──
        _virtualMidiPort.DawMidiReceived += bytes =>
        {
            var active = _activeTransport;
            if (active != null)
                _ = active.SendMidiAsync(bytes);
        };
    }

    /// <summary>
    /// Wire up the three transport events. Only StateChanged from the active
    /// transport updates the main UI state; MidiDataReceived and DeviceDiscovered
    /// are processed from any transport.
    /// </summary>
    private void SubscribeTransport(IMidiTransport transport)
    {
        transport.DeviceDiscovered += device =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (!DiscoveredDevices.Any(d => d.Id == device.Id))
                    DiscoveredDevices.Add(device);
            });
        };

        transport.MidiDataReceived += rawBytes =>
        {
            // Forward to virtual MIDI port so DAW receives the data
            _virtualMidiPort.SendMidi(rawBytes);

            var msg = MidiParser.Parse(rawBytes, MidiDirection.PhoneToDaw, transport.Type);
            if (msg != null) AddMidiMessage(msg);
        };

        transport.StateChanged += state =>
        {
            // Only the active transport drives the connection UI
            if (!ReferenceEquals(transport, _activeTransport)) return;

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                ConnectionState = state;
                if (state == ConnectionState.Error)
                {
                    StatusText      = "Error: Connection lost";
                    StatusDotColor  = "#EF5350";
                    ConnectedDeviceName = null;
                    TransportBadge  = "";
                    LatencyMs       = 0;
                }
            });
        };
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

    /// <summary>
    /// Run system detection on startup.
    /// </summary>
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

    /// <summary>
    /// Skip setup wizard (user already has everything configured).
    /// </summary>
    [RelayCommand]
    private void SkipSetup()
    {
        ShowSetupWizard = false;
    }

    /// <summary>
    /// Start scanning for GearBoard devices across all available transports.
    /// </summary>
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
            IsScanning     = false;
            StatusText     = "All transport scans failed. Check Bluetooth and network.";
            StatusDotColor = "#EF5350";
            ConnectionState = ConnectionState.Idle;
            return;
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

    /// <summary>
    /// Connect to a discovered device using the transport matching the device's transport type.
    /// </summary>
    [RelayCommand]
    private async Task ConnectToDeviceAsync(DeviceInfo? device)
    {
        if (device == null) return;

        StatusText         = $"Connecting to {device.Name}...";
        StatusIcon         = "◐";
        StatusDotColor     = "#FFA726";
        ActiveTransportType = device.Transport;

        _activeTransport = _transports.FirstOrDefault(t => t.Type == device.Transport);
        if (_activeTransport == null)
        {
            _logger.LogWarning("No transport registered for type {Type}", device.Transport);
            StatusText     = $"No handler for transport {device.Transport}";
            StatusDotColor = "#EF5350";
            return;
        }

        var ok = await _activeTransport.ConnectAsync(device);

        if (ok)
        {
            ConnectedDeviceName = device.Name;
            StatusText     = $"Connected — {device.Name}";
            StatusIcon     = "●";
            StatusDotColor = "#4CAF50";
            TransportBadge = device.TransportIcon;
            LatencyMs      = _activeTransport.EstimatedLatencyMs;
        }
        else
        {
            StatusText     = $"Failed to connect to {device.Name}";
            StatusDotColor = "#EF5350";
            _activeTransport = null;
        }
    }

    /// <summary>
    /// Disconnect from the currently active transport.
    /// </summary>
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
