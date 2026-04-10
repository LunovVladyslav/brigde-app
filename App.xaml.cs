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

        // Services
        services.AddSingleton<SystemDetector>();
        services.AddSingleton<DriverInstaller>();
        services.AddSingleton<BleMidiService>();
        services.AddSingleton<UsbMidiService>();
        services.AddSingleton<WifiMidiService>();
        services.AddSingleton<VirtualMidiPortService>();

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

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            // Setup is done — skip the inline wizard panel in the main window
            var mainVm = (MainViewModel)mainWindow.DataContext;
            mainVm.ShowSetupWizard = false;
            mainWindow.Show();
        };

        setupWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
