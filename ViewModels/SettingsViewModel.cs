using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GearBoardBridge.Models;
using GearBoardBridge.Services;

namespace GearBoardBridge.ViewModels;

/// <summary>
/// ViewModel for the Settings window.
/// Exposes all user-configurable options and saves them via <see cref="SettingsService"/>.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;

    // ── Auto-Connect ─────────────────────────────────────────────────────────

    [ObservableProperty] private bool _autoConnectOnStartup;
    [ObservableProperty] private string _lastDeviceSummary = "None";

    // ── Startup ───────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _startWithWindows;

    // ── Virtual MIDI ──────────────────────────────────────────────────────────

    [ObservableProperty] private string _virtualPortName = "GearBoard MIDI";

    // ── WiFi ──────────────────────────────────────────────────────────────────

    [ObservableProperty] private int _wifiPort = 5004;

    // ── Transport Priority ────────────────────────────────────────────────────

    [ObservableProperty] private bool _priorityAuto    = true;
    [ObservableProperty] private bool _priorityWifi    = false;
    [ObservableProperty] private bool _priorityBle     = false;

    /// <summary>Fired when the user saves settings — caller can close the window.</summary>
    public event Action? SettingsSaved;

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        var s = _settingsService.Current;

        AutoConnectOnStartup = s.AutoConnectOnStartup;
        LastDeviceSummary    = s.LastDeviceName is not null
            ? $"{s.LastDeviceName}  ({s.LastTransportType})"
            : "None";

        StartMinimized   = s.StartMinimized;
        StartWithWindows = _settingsService.IsStartWithWindowsEnabled();

        VirtualPortName = s.VirtualPortName;
        WifiPort        = s.WifiPort;

        PriorityAuto = s.TransportPriority == TransportPriorityMode.Auto;
        PriorityWifi = s.TransportPriority == TransportPriorityMode.WifiFirst;
        PriorityBle  = s.TransportPriority == TransportPriorityMode.BleFirst;
    }

    [RelayCommand]
    private void Save()
    {
        var s = _settingsService.Current;

        s.AutoConnectOnStartup = AutoConnectOnStartup;
        s.StartMinimized       = StartMinimized;
        s.VirtualPortName      = string.IsNullOrWhiteSpace(VirtualPortName)
                                     ? "GearBoard MIDI"
                                     : VirtualPortName.Trim();
        s.WifiPort             = WifiPort is >= 1024 and <= 65535 ? WifiPort : 5004;

        s.TransportPriority    = PriorityWifi ? TransportPriorityMode.WifiFirst
                               : PriorityBle  ? TransportPriorityMode.BleFirst
                               :                TransportPriorityMode.Auto;

        _settingsService.Save();
        _settingsService.ApplyStartWithWindows(StartWithWindows);

        SettingsSaved?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => SettingsSaved?.Invoke();

    [RelayCommand]
    private void ClearLastDevice()
    {
        _settingsService.ClearLastDevice();
        LastDeviceSummary    = "None";
        AutoConnectOnStartup = false;
    }
}
