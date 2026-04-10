using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GearBoardBridge.Services;
using GearBoardBridge.ViewModels;
using GearBoardBridge.Views;

namespace GearBoardBridge;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
        });

        // ── Transport services ───────────────────────────────────────────────
        services.AddSingleton<SystemDetector>();
        services.AddSingleton<DriverInstaller>();
        services.AddSingleton<BleMidiService>();
        services.AddSingleton<UsbMidiService>();
        services.AddSingleton<WifiMidiService>();
        services.AddSingleton<VirtualMidiPortService>();

        // ── Orchestration (Phase 8) ──────────────────────────────────────────
        services.AddSingleton<TransportManager>();   // priority logic + discovery
        services.AddSingleton<MidiBridge>();          // MIDI routing
        services.AddSingleton<TrayIconManager>();     // minimize-to-tray

        // ── Settings (Phase 9) ───────────────────────────────────────────────
        services.AddSingleton<SettingsService>();

        // ── ViewModels ───────────────────────────────────────────────────────
        services.AddTransient<MainViewModel>();
        services.AddTransient<SetupViewModel>();
        services.AddTransient<SettingsViewModel>();

        // ── Views ────────────────────────────────────────────────────────────
        services.AddSingleton<MainWindow>();
        services.AddTransient<SetupWindow>();
        services.AddTransient<SettingsWindow>();

        _serviceProvider = services.BuildServiceProvider();

        // Load persisted settings before anything else
        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
        settingsService.Load();

        // --minimized flag (from startup registry) or setting
        bool launchMinimized = e.Args.Contains("--minimized") || settingsService.Current.StartMinimized;

        // ── Show Setup Wizard first, then Main Window ─────────────────────────
        var setupWindow = _serviceProvider.GetRequiredService<SetupWindow>();
        var setupVm     = (SetupViewModel)setupWindow.DataContext;

        setupVm.SetupCompleted += async () =>
        {
            setupWindow.Close();

            // Open virtual MIDI port so DAWs can see it
            var portName = settingsService.Current.VirtualPortName;
            var midiPort = _serviceProvider.GetRequiredService<VirtualMidiPortService>();
            midiPort.Open(portName);

            // Activate MIDI routing (Phase 8)
            _ = _serviceProvider.GetRequiredService<MidiBridge>();

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            var mainVm     = (MainViewModel)mainWindow.DataContext;
            mainVm.ShowSetupWizard = false;

            // Wire Settings button (Phase 9)
            mainWindow.SettingsRequested += () =>
            {
                var settingsWin = _serviceProvider.GetRequiredService<SettingsWindow>();
                settingsWin.Owner = mainWindow;
                settingsWin.ShowDialog();
            };

            if (launchMinimized)
                mainWindow.WindowState = WindowState.Minimized;

            mainWindow.Show();

            // Attach tray icon after window is visible (Phase 8)
            var tray = _serviceProvider.GetRequiredService<TrayIconManager>();
            tray.Attach(mainWindow);
            tray.DisconnectRequested += async () => await mainVm.DisconnectCommand.ExecuteAsync(null);

            // Auto-connect to last device (Phase 9)
            await mainVm.TryAutoConnectOnStartupAsync();
        };

        setupWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
