using System.Windows;
using GearBoardBridge.Services;
using GearBoardBridge.ViewModels;

namespace GearBoardBridge.Views;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings;

    /// <summary>Fired when the user clicks the Settings button.</summary>
    public event Action? SettingsRequested;

    public MainWindow(MainViewModel viewModel, SettingsService settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settings   = settings;

        // Restore saved window position
        RestoreWindowPosition();

        // Persist position on every move/size change
        LocationChanged += (_, _) => SaveWindowPosition();
    }

    private void RestoreWindowPosition()
    {
        var left = _settings.Current.WindowLeft;
        var top  = _settings.Current.WindowTop;

        if (double.IsNaN(left) || double.IsNaN(top)) return;

        // Validate that the position is still on a visible screen
        var screenWidth  = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;
        var screenLeft   = SystemParameters.VirtualScreenLeft;
        var screenTop    = SystemParameters.VirtualScreenTop;

        if (left >= screenLeft && left < screenLeft + screenWidth - 100 &&
            top  >= screenTop  && top  < screenTop  + screenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top  = top;
        }
    }

    private void SaveWindowPosition()
    {
        if (WindowState != WindowState.Normal) return;
        _settings.Current.WindowLeft = Left;
        _settings.Current.WindowTop  = Top;
        _settings.Save();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke();
}
