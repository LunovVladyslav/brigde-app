using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GearBoardBridge.Services;

/// <summary>
/// Handles detection and installation guidance for the teVirtualMIDI driver (loopMIDI).
/// On Windows 11 with Windows MIDI Services, no driver is needed.
/// </summary>
public class DriverInstaller
{
    private readonly ILogger<DriverInstaller> _logger;

    private const string LoopMidiDownloadUrl =
        "https://www.tobias-erichsen.de/software/loopmidi.html";

    public DriverInstaller(ILogger<DriverInstaller> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns true if teVirtualMIDI (loopMIDI) driver is installed.
    /// </summary>
    public bool IsDriverInstalled()
    {
        try
        {
            // Primary: check registry key written by loopMIDI installer
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Tobias Erichsen\teVirtualMIDI");
            if (key != null)
            {
                _logger.LogInformation("teVirtualMIDI driver found in registry");
                return true;
            }

            // Fallback: check install directory
            var installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Tobias Erichsen", "loopMIDI");
            if (Directory.Exists(installDir))
            {
                _logger.LogInformation("loopMIDI install directory found");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking driver installation");
            return false;
        }
    }

    /// <summary>
    /// Opens the loopMIDI download page in the default browser.
    /// </summary>
    public void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LoopMidiDownloadUrl,
                UseShellExecute = true
            });
            _logger.LogInformation("Opened loopMIDI download page");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open download page");
        }
    }
}
