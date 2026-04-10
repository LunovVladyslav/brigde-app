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

        // Services
        services.AddSingleton<SystemDetector>();
        services.AddSingleton<DriverInstaller>();
        services.AddSingleton<BleMidiService>();
        services.AddSingleton<UsbMidiService>();
        services.AddSingleton<WifiMidiService>();
        services.AddSingleton<VirtualMidiPortService>();
        services.AddSingleton<SettingsService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<SetupViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
        services.AddTransient<SetupWindow>();
        services.AddTransient<SettingsWindow>();

        _serviceProvider = services.BuildServiceProvider();

        // Load persisted settings before anything else
        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
        settingsService.Load();

        // Check if launched with --minimized flag (from startup registry entry)
        bool launchMinimized = e.Args.Contains("--minimized") || settingsService.Current.StartMinimized;

        var setupWindow = _serviceProvider.GetRequiredService<SetupWindow>();
        var setupVm     = (SetupViewModel)setupWindow.DataContext;

        setupVm.SetupCompleted += async () =>
        {
            setupWindow.Close();

            var portName = settingsService.Current.VirtualPortName;
            var midiPort = _serviceProvider.GetRequiredService<VirtualMidiPortService>();
            midiPort.Open(portName);

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            var mainVm     = (MainViewModel)mainWindow.DataContext;
            mainVm.ShowSetupWizard = false;

            // Wire Settings button
            mainWindow.SettingsRequested += () =>
            {
                var settingsWin = _serviceProvider.GetRequiredService<SettingsWindow>();
                settingsWin.Owner = mainWindow;
                settingsWin.ShowDialog();
            };

            if (launchMinimized)
                mainWindow.WindowState = WindowState.Minimized;

            mainWindow.Show();

            // Auto-connect after window is visible
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
