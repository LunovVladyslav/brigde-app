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

        // ── Build DI Container ──
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
        });

        // Services — individual transports must be registered before TransportManager
        services.AddSingleton<SystemDetector>();
        services.AddSingleton<DriverInstaller>();
        services.AddSingleton<BleMidiService>();
        services.AddSingleton<UsbMidiService>();
        services.AddSingleton<WifiMidiService>();
        services.AddSingleton<VirtualMidiPortService>();
        services.AddSingleton<TransportManager>();
        services.AddSingleton<MidiBridge>();
        services.AddSingleton<TrayIconManager>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<SetupViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
        services.AddTransient<SetupWindow>();

        _serviceProvider = services.BuildServiceProvider();

        // ── Show Setup Wizard first, then Main Window ──
        var setupWindow = _serviceProvider.GetRequiredService<SetupWindow>();
        var setupVm     = (SetupViewModel)setupWindow.DataContext;

        setupVm.SetupCompleted += () =>
        {
            setupWindow.Close();

            // Open virtual MIDI port so DAWs can see "GearBoard MIDI"
            var midiPort = _serviceProvider.GetRequiredService<VirtualMidiPortService>();
            midiPort.Open("GearBoard MIDI");

            // Resolve MidiBridge to activate MIDI routing (singleton, wires itself on construction)
            _ = _serviceProvider.GetRequiredService<MidiBridge>();

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            var mainVm     = (MainViewModel)mainWindow.DataContext;
            mainVm.ShowSetupWizard = false;
            mainWindow.Show();

            // Attach tray icon after the window is visible
            var tray = _serviceProvider.GetRequiredService<TrayIconManager>();
            tray.Attach(mainWindow);
            tray.DisconnectRequested += async () => await mainVm.DisconnectCommand.ExecuteAsync(null);
        };

        setupWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
