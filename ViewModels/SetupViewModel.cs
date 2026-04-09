using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GearBoardBridge.Models;
using GearBoardBridge.Services;
using Microsoft.Extensions.Logging;

namespace GearBoardBridge.ViewModels;

public enum SetupStep
{
    SystemCheck = 0,
    DriverSetup  = 1,
    Ready        = 2
}

/// <summary>
/// ViewModel for the 3-step first-launch setup wizard.
/// </summary>
public partial class SetupViewModel : ObservableObject
{
    private readonly SystemDetector _systemDetector;
    private readonly DriverInstaller _driverInstaller;
    private readonly ILogger<SetupViewModel> _logger;

    // ── Wizard state ──
    [ObservableProperty] private SetupStep _currentStep = SetupStep.SystemCheck;
    [ObservableProperty] private bool _isStep0Active = true;
    [ObservableProperty] private bool _isStep1Active;
    [ObservableProperty] private bool _isStep2Active;

    // ── System check ──
    [ObservableProperty] private SystemCheckResult? _systemCheck;
    [ObservableProperty] private bool _isRunningCheck;
    [ObservableProperty] private bool _checkComplete;
    [ObservableProperty] private string _checkStatusMessage = "";

    // ── Driver step ──
    [ObservableProperty] private bool _driverInstalled;
    [ObservableProperty] private bool _win11MidiServices;
    [ObservableProperty] private bool _virtualMidiReady;  // driver OR Win11 MIDI Services

    /// <summary>Raised when the user clicks Launch or Skip — App shows MainWindow.</summary>
    public event Action? SetupCompleted;

    public SetupViewModel(
        SystemDetector systemDetector,
        DriverInstaller driverInstaller,
        ILogger<SetupViewModel> logger)
    {
        _systemDetector  = systemDetector;
        _driverInstaller = driverInstaller;
        _logger          = logger;
    }

    // ── Commands ────────────────────────────────────

    [RelayCommand]
    private async Task RunCheckAsync()
    {
        IsRunningCheck   = true;
        CheckComplete    = false;
        CheckStatusMessage = "Checking system…";

        try
        {
            SystemCheck      = await _systemDetector.DetectAsync();
            DriverInstalled  = _driverInstaller.IsDriverInstalled();
            Win11MidiServices = SystemCheck.HasWindowsMidiServices;
            VirtualMidiReady = DriverInstalled || Win11MidiServices;

            CheckStatusMessage = SystemCheck.AnyTransportAvailable
                ? "System check complete."
                : "No transport detected — at least WiFi or BLE is recommended.";

            CheckComplete = true;
            NextCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "System check failed");
            CheckStatusMessage = "Check failed. Please try again.";
        }
        finally
        {
            IsRunningCheck = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        if (CurrentStep == SetupStep.SystemCheck)
            GoToStep(VirtualMidiReady ? SetupStep.Ready : SetupStep.DriverSetup);
        else if (CurrentStep == SetupStep.DriverSetup)
            GoToStep(SetupStep.Ready);
    }

    private bool CanGoNext() => CheckComplete;

    [RelayCommand]
    private void Back()
    {
        if (CurrentStep == SetupStep.DriverSetup)
            GoToStep(SetupStep.SystemCheck);
        else if (CurrentStep == SetupStep.Ready)
            GoToStep(VirtualMidiReady ? SetupStep.SystemCheck : SetupStep.DriverSetup);
    }

    [RelayCommand]
    private void OpenDriverDownload() => _driverInstaller.OpenDownloadPage();

    [RelayCommand]
    private void RecheckDriver()
    {
        DriverInstalled  = _driverInstaller.IsDriverInstalled();
        VirtualMidiReady = DriverInstalled || Win11MidiServices;
        if (VirtualMidiReady)
            CheckStatusMessage = "Driver detected — you can continue.";
    }

    [RelayCommand]
    private void Launch() => SetupCompleted?.Invoke();

    [RelayCommand]
    private void Skip() => SetupCompleted?.Invoke();

    // ── Helpers ──────────────────────────────────────

    private void GoToStep(SetupStep step)
    {
        CurrentStep   = step;
        IsStep0Active = step == SetupStep.SystemCheck;
        IsStep1Active = step == SetupStep.DriverSetup;
        IsStep2Active = step == SetupStep.Ready;
    }
}
