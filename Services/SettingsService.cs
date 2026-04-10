using System.IO;
using System.Text.Json;
using GearBoardBridge.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GearBoardBridge.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to JSON at
/// <c>%AppData%\Local\GearBoardBridge\settings.json</c>.
/// Also manages the Windows startup registry entry.
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "GearBoardBridge");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented         = true,
        PropertyNamingPolicy  = JsonNamingPolicy.CamelCase,
    };

    private const string RunKeyPath    = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName  = "GearBoardBridge";

    private readonly ILogger<SettingsService> _logger;

    public AppSettings Current { get; private set; } = new();

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
    }

    // ── Load / Save ───────────────────────────────────────────────────────────

    /// <summary>Load settings from disk. Falls back to defaults if file is missing or corrupt.</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                _logger.LogInformation("No settings file found — using defaults.");
                Current = new AppSettings();
                return;
            }

            var json = File.ReadAllText(SettingsPath);
            Current  = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            _logger.LogInformation("Settings loaded from {Path}", SettingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings — using defaults.");
            Current = new AppSettings();
        }
    }

    /// <summary>Persist current settings to disk.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(Current, JsonOpts);
            File.WriteAllText(SettingsPath, json);
            _logger.LogDebug("Settings saved to {Path}", SettingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save settings.");
        }
    }

    /// <summary>Save the last connected device for auto-connect on next launch.</summary>
    public void SaveLastDevice(string name, string id, TransportType transport)
    {
        Current.LastDeviceName    = name;
        Current.LastDeviceId      = id;
        Current.LastTransportType = transport;
        Save();
    }

    /// <summary>Clear the saved device (e.g. after intentional disconnect).</summary>
    public void ClearLastDevice()
    {
        Current.LastDeviceName    = null;
        Current.LastDeviceId      = null;
        Current.LastTransportType = null;
        Save();
    }

    // ── Start with Windows ────────────────────────────────────────────────────

    /// <summary>
    /// Add or remove the GearBoard Bridge entry in the Windows startup registry key.
    /// </summary>
    public void ApplyStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                _logger.LogWarning("Could not open registry Run key.");
                return;
            }

            if (enable)
            {
                var exePath = Environment.ProcessPath ?? AppContext.BaseDirectory + "GearBoardBridge.exe";
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(RunValueName, $"\"{exePath}\" --minimized");
                    _logger.LogInformation("Start-with-Windows enabled.");
                }
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                _logger.LogInformation("Start-with-Windows disabled.");
            }

            Current.StartWithWindows = enable;
            Save();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update start-with-Windows registry entry.");
        }
    }

    /// <summary>Check if the registry startup entry currently exists.</summary>
    public bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the transport priority order as an ordered list.
    /// </summary>
    public IReadOnlyList<TransportType> GetPriorityOrder() => Current.TransportPriority switch
    {
        TransportPriorityMode.WifiFirst => [TransportType.WiFi, TransportType.USB, TransportType.BLE],
        TransportPriorityMode.BleFirst  => [TransportType.BLE,  TransportType.WiFi, TransportType.USB],
        _                               => [TransportType.USB,  TransportType.WiFi, TransportType.BLE],
    };
}
