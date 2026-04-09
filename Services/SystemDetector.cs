using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using GearBoardBridge.Models;

namespace GearBoardBridge.Services;

/// <summary>
/// Detects system capabilities on startup.
/// Checks Windows version, Bluetooth adapter, USB MIDI devices,
/// WiFi connectivity, and virtual MIDI driver availability.
/// </summary>
public class SystemDetector
{
    private readonly ILogger<SystemDetector> _logger;

    public SystemDetector(ILogger<SystemDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Run all system checks and return combined results.
    /// </summary>
    public async Task<SystemCheckResult> DetectAsync()
    {
        var result = new SystemCheckResult();

        // Windows version
        DetectWindowsVersion(result);

        // Run async checks in parallel
        await Task.WhenAll(
            DetectBluetoothAsync(result),
            DetectUsbMidiAsync(result)
        );

        // WiFi
        DetectWifi(result);

        // Virtual MIDI driver
        DetectVirtualMidiDriver(result);

        _logger.LogInformation($"System check: Win={result.WindowsVersion}, " +
            $"BT={result.HasBluetoothAdapter}, BLE={result.HasBleSupport}, " +
            $"USB={result.HasUsbMidiDevice}, WiFi={result.HasWifiConnection}, " +
            $"VirtualMIDI={result.HasVirtualMidiDriver}");

        return result;
    }

    private void DetectWindowsVersion(SystemCheckResult result)
    {
        var os = Environment.OSVersion;
        result.IsWindows10OrLater = os.Version.Major >= 10;
        result.IsWindows11 = os.Version.Major >= 10 && os.Version.Build >= 22000;
        result.WindowsVersion = result.IsWindows11
            ? $"Windows 11 (Build {os.Version.Build})"
            : $"Windows {os.Version.Major} (Build {os.Version.Build})";

        _logger.LogInformation($"Windows: {result.WindowsVersion}");
    }

    private async Task DetectBluetoothAsync(SystemCheckResult result)
    {
        try
        {
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            foreach (var radio in radios)
            {
                if (radio.Kind == Windows.Devices.Radios.RadioKind.Bluetooth)
                {
                    result.HasBluetoothAdapter = true;
                    result.BluetoothAdapterName = radio.Name;
                    result.IsBluetoothEnabled = radio.State == Windows.Devices.Radios.RadioState.On;
                    result.HasBleSupport = true; // If we have BT radio, BLE is supported on Win10+
                    _logger.LogInformation($"Bluetooth: {radio.Name}, Enabled={result.IsBluetoothEnabled}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect Bluetooth");
        }
    }

    private async Task DetectUsbMidiAsync(SystemCheckResult result)
    {
        try
        {
            var selector = Windows.Devices.Midi.MidiInPort.GetDeviceSelector();
            var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);

            foreach (var device in devices)
            {
                // Prefer GearBoard/Android-named devices, but flag any USB MIDI device found
                result.HasUsbMidiDevice = true;
                result.UsbMidiDeviceName = device.Name;
                _logger.LogInformation($"USB MIDI device found: {device.Name}");

                if (device.Name.Contains("GearBoard", StringComparison.OrdinalIgnoreCase) ||
                    device.Name.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
                    device.Name.Contains("MIDI Function", StringComparison.OrdinalIgnoreCase))
                {
                    break; // Prioritise GearBoard match
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect USB MIDI devices");
        }
    }

    private void DetectWifi(SystemCheckResult result)
    {
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                // Check for WiFi or Ethernet (both work for WiFi MIDI)
                if (iface.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.Ethernet)
                {
                    foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            result.HasWifiConnection = true;
                            result.LocalIpAddress = addr.Address.ToString();
                            result.WifiNetworkName = iface.Name;
                            _logger.LogInformation($"Network: {iface.Name} at {result.LocalIpAddress}");
                            return;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect WiFi");
        }
    }

    private void DetectVirtualMidiDriver(SystemCheckResult result)
    {
        try
        {
            // Check for teVirtualMIDI driver (loopMIDI)
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Tobias Erichsen\teVirtualMIDI");
            result.HasVirtualMidiDriver = key != null;

            if (!result.HasVirtualMidiDriver)
            {
                // Also check program files
                var loopMidiPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Tobias Erichsen", "loopMIDI");
                result.HasVirtualMidiDriver = Directory.Exists(loopMidiPath);
            }

            _logger.LogInformation($"Virtual MIDI driver: {result.HasVirtualMidiDriver}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect virtual MIDI driver");
        }

        // Check for Windows MIDI Services (Win11)
        if (result.IsWindows11)
        {
            // Windows MIDI Services is available on Win11 with recent updates
            result.HasWindowsMidiServices = true;
        }
    }
}
