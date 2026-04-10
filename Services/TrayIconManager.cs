using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GearBoardBridge.Models;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Logging;

namespace GearBoardBridge.Services;

/// <summary>
/// Manages the system tray icon, context menu, and minimize-to-tray behavior.
///
/// Usage:
///   1. Call <see cref="Attach"/> once the main window is ready.
///   2. Call <see cref="UpdateTransportStatus"/> whenever connection state changes.
///   3. Dispose to clean up the tray icon on exit.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly ILogger<TrayIconManager> _logger;
    private TaskbarIcon? _taskbarIcon;
    private Window? _mainWindow;
    private bool _isMinimizedToTray;

    // Menu items updated dynamically
    private MenuItem? _statusMenuItem;
    private MenuItem? _disconnectMenuItem;

    public TrayIconManager(ILogger<TrayIconManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attach the tray manager to <paramref name="window"/>.
    /// Call this once the window is initialized.
    /// </summary>
    public void Attach(Window window)
    {
        _mainWindow = window;
        _mainWindow.Closing += OnWindowClosing;

        CreateTrayIcon();
        _logger.LogInformation("Tray icon created.");
    }

    /// <summary>
    /// Update the tray icon tooltip and status menu item to reflect the current connection state.
    /// </summary>
    public void UpdateTransportStatus(ConnectionState state, string? deviceName, TransportType? activeTransport)
    {
        if (_taskbarIcon is null) return;

        var statusText = state switch
        {
            ConnectionState.Connected   => $"Connected — {deviceName} ({activeTransport})",
            ConnectionState.Scanning    => "Scanning...",
            ConnectionState.Connecting  => $"Connecting to {deviceName}...",
            ConnectionState.Reconnecting => $"Reconnecting to {deviceName}...",
            ConnectionState.Error       => "Connection error",
            _                           => "Not connected"
        };

        Application.Current?.Dispatcher.Invoke(() =>
        {
            _taskbarIcon.ToolTipText = $"GearBoard Bridge — {statusText}";
            if (_statusMenuItem is not null)
                _statusMenuItem.Header = statusText;
            if (_disconnectMenuItem is not null)
                _disconnectMenuItem.IsEnabled = state == ConnectionState.Connected;
        });
    }

    // ── Minimize / Restore ────────────────────────────────────────────────────

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Intercept close → minimize to tray instead
        e.Cancel = true;
        MinimizeToTray();
    }

    private void MinimizeToTray()
    {
        if (_mainWindow is null) return;
        _mainWindow.Hide();
        _isMinimizedToTray = true;
        _taskbarIcon?.ShowBalloonTip(
            "GearBoard Bridge",
            "Running in the background. Double-click the tray icon to restore.",
            BalloonIcon.Info);
    }

    private void RestoreWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _isMinimizedToTray = false;
    }

    // ── Tray icon construction ────────────────────────────────────────────────

    private void CreateTrayIcon()
    {
        var contextMenu = BuildContextMenu();

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText  = "GearBoard Bridge — Not connected",
            ContextMenu  = contextMenu,
            // Use the application icon as placeholder; replace with a bundled .ico for release
            Icon         = System.Drawing.SystemIcons.Application,
        };

        _taskbarIcon.TrayMouseDoubleClick += (_, _) => RestoreWindow();
        _taskbarIcon.TrayLeftMouseDown    += (_, _) =>
        {
            if (_isMinimizedToTray) RestoreWindow();
        };
    }

    private ContextMenu BuildContextMenu()
    {
        _statusMenuItem = new MenuItem
        {
            Header    = "Not connected",
            IsEnabled = false,
        };

        var showItem = new MenuItem { Header = "Show GearBoard Bridge" };
        showItem.Click += (_, _) => RestoreWindow();

        _disconnectMenuItem = new MenuItem
        {
            Header    = "Disconnect",
            IsEnabled = false,
        };
        _disconnectMenuItem.Click += (_, _) => DisconnectRequested?.Invoke();

        var separator = new Separator();

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) =>
        {
            // Detach the closing handler so we actually exit
            if (_mainWindow is not null)
                _mainWindow.Closing -= OnWindowClosing;
            Application.Current.Shutdown();
        };

        return new ContextMenu
        {
            Items =
            {
                _statusMenuItem,
                new Separator(),
                showItem,
                _disconnectMenuItem,
                separator,
                exitItem,
            }
        };
    }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fired when the user clicks "Disconnect" in the tray context menu.</summary>
    public event Action? DisconnectRequested;

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_mainWindow is not null)
            _mainWindow.Closing -= OnWindowClosing;
        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
    }
}
