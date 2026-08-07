using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Local-only USB / peripheral / storage hotplug tracker (SQLite, no server sync yet).
///
/// Tracks when physical devices are plugged in / unplugged:
///   - udev monitor (subsystem=usb) gives real-time add/remove events,
///   - a periodic /sys enumeration backfills anything the monitor missed (devices that
///     were present at boot before this service started) and self-heals gaps.
///
/// Each plug-in writes a <c>hardware_devices</c> row (device_class/vendor/product/serial/
/// bus_path/device_node/plugged_at); the matching plug-out sets <c>unplugged_at</c>.
/// The open-row unique index (bus_path) keeps exactly one open row per physical slot.
/// </summary>
public class HardwareDeviceWatcherService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly ILogger<HardwareDeviceWatcherService> _logger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(5);

    public HardwareDeviceWatcherService(AppConfig config, ILogStore store, ILogger<HardwareDeviceWatcherService> logger)
    {
        _config = config;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux() || !_config.HardwareDevicesEnabled)
        {
            _logger.LogInformation("Hardware device watcher disabled (platform={Platform}, enabled={Enabled})",
                OperatingSystem.IsLinux() ? "linux" : "non-linux", _config.HardwareDevicesEnabled);
            return;
        }

        await _store.InitializeAsync(stoppingToken);

        _logger.LogInformation("Hardware device watcher starting");

        try
        {
            await ReconcileAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial hardware device enumeration failed");
        }

        var monitorTask = Task.Run(() => RunUdevMonitorAsync(stoppingToken), stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ReconcileInterval, stoppingToken);
                    await ReconcileAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Hardware device reconcile failed");
                }
            }
        }
        finally
        {
            try { await monitorTask; } catch { }
        }

        _logger.LogInformation("Hardware device watcher stopped");
    }

    // ────────────────────────────────────────
    // udev monitor — real-time add/remove
    // ────────────────────────────────────────

    private async Task RunUdevMonitorAsync(CancellationToken ct)
    {
        if (!CommandExists("udevadm"))
        {
            _logger.LogDebug("udevadm not found — relying on periodic /sys reconcile only");
            return;
        }

        using var proc = StartProcess("udevadm", "monitor --property --subsystem-match=usb");
        if (proc == null)
        {
            _logger.LogDebug("Failed to start udevadm monitor");
            return;
        }

        _logger.LogDebug("udevadm monitor started");

        string? pendingAction = null;
        string? pendingDevPath = null;
        string? pendingSubsystem = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;

                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    if (!string.IsNullOrEmpty(pendingAction) && !string.IsNullOrEmpty(pendingDevPath))
                    {
                        try { await HandleUdevEventAsync(pendingAction, pendingDevPath, pendingSubsystem, ct); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Failed to handle udev event"); }
                    }
                    pendingAction = null;
                    pendingDevPath = null;
                    pendingSubsystem = null;
                    continue;
                }

                if (trimmed.StartsWith("ACTION=", StringComparison.Ordinal))
                    pendingAction = trimmed["ACTION=".Length..];
                else if (trimmed.StartsWith("DEVPATH=", StringComparison.Ordinal))
                    pendingDevPath = trimmed["DEVPATH=".Length..];
                else if (trimmed.StartsWith("SUBSYSTEM=", StringComparison.Ordinal))
                    pendingSubsystem = trimmed["SUBSYSTEM=".Length..];
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "udevadm monitor ended");
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { proc.Dispose(); } catch { }
        }
    }

    private async Task HandleUdevEventAsync(string action, string devPath, string? subsystem, CancellationToken ct)
    {
        switch (action)
        {
            case "add":
            case "bind":
                // Interfaces (…:1.0) and non-usb subsystems arrive too — a full re-scan
                // is idempotent (unique open-path index + store dedup), so just reconcile.
                await ReconcileAsync(ct);
                break;

            case "remove":
            case "unbind":
                // udev may emit the interface path (…:1.0) for the removal of the whole
                // device, so close every open row whose bus_path is this path OR a prefix
                // of this path (the device node is a parent of its interfaces).
                await CloseDevicesByPathAsync(devPath, ct);
                break;
        }
    }

    private async Task CloseDevicesByPathAsync(string devPath, CancellationToken ct)
    {
        var open = await _store.GetOpenHardwareDevicesAsync(ct);
        var now = DateTime.UtcNow;
        var matched = 0;
        foreach (var dev in open)
        {
            if (string.IsNullOrEmpty(dev.BusPath)) continue;
            // Match on component boundaries only: the device itself, or an interface under
            // it (…:1.0) being removed means the whole device is gone. NEVER treat a
            // device path as a match for an ANCESTOR row (a removed device must not close
            // the host-controller row it hangs off).
            if (dev.BusPath == devPath || devPath.StartsWith(dev.BusPath + "/", StringComparison.Ordinal))
            {
                await _store.CloseHardwareDeviceAsync(dev.Id, now, ct);
                matched++;
                _logger.LogInformation("Hardware device unplugged: {Class} {Product} ({BusPath})",
                    dev.DeviceClass, string.IsNullOrEmpty(dev.Product) ? "(unnamed)" : dev.Product, dev.BusPath);
            }
        }
        if (matched > 0)
        {
            _logger.LogDebug("Closed {Count} hardware device row(s) for DEVPATH {Path}", matched, devPath);
        }
    }

    // ────────────────────────────────────────
    // /sys enumeration (boot backfill + reconcile)
    // ────────────────────────────────────────

    private async Task ReconcileAsync(CancellationToken ct)
    {
        await _reconcileGate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var devices = new List<HardwareDevice>();

            // 1) USB devices with a real idVendor (skip interface sub-directories 1-8:1.0
            //    AND root hubs / host controllers usb1/usb2 — internal, always present,
            //    and a prefix of every device under them, which would make unplug matching
            //    close the controller row whenever any device is removed).
            var usbRoot = "/sys/bus/usb/devices";
            if (Directory.Exists(usbRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(usbRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (Regex.IsMatch(name, "^usb\\d+$")) continue;
                    var idVendor = ReadSysFile(Path.Combine(dir, "idVendor"));
                    if (string.IsNullOrWhiteSpace(idVendor)) continue;

                    var vendor = ReadSysFile(Path.Combine(dir, "manufacturer"));
                    var product = ReadSysFile(Path.Combine(dir, "product"));
                    var serial = ReadSysFile(Path.Combine(dir, "serial"));
                    var devClass = ClassifyUsbDevice(dir);

                    devices.Add(new HardwareDevice
                    {
                        DeviceClass = devClass,
                        Vendor = vendor ?? string.Empty,
                        Product = product ?? string.Empty,
                        Serial = serial ?? string.Empty,
                        BusPath = ResolveSysfsPath(dir),
                        DeviceNode = ReadSysFile(Path.Combine(dir, "devnum")) is { } dn && dn.Length > 0
                            ? $"/dev/bus/usb/{ReadSysFile(Path.Combine(dir, "busnum"))}/{dn}"
                            : string.Empty,
                        PluggedAt = now,
                    });
                }
            }

            // 2) Storage (block) devices that live on USB (external drives stick out).
            var blockRoot = "/sys/class/block";
            if (Directory.Exists(blockRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(blockRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal)) continue;
                    var linkTarget = ResolveSysfsPath(dir);
                    if (string.IsNullOrEmpty(linkTarget) || !linkTarget.Contains("/usb", StringComparison.Ordinal)) continue;

                    var vendor = ReadSysFile(Path.Combine(dir, "device/vendor")) ?? string.Empty;
                    var product = ReadSysFile(Path.Combine(dir, "device/model")) ?? string.Empty;
                    devices.Add(new HardwareDevice
                    {
                        DeviceClass = "storage",
                        Vendor = vendor,
                        Product = string.IsNullOrWhiteSpace(product) ? name : product.Trim(),
                        Serial = string.Empty,
                        BusPath = linkTarget,
                        DeviceNode = $"/dev/{name}",
                        PluggedAt = now,
                    });
                }
            }

            if (devices.Count > 0)
            {
                await _store.StoreHardwareDevicesAsync(devices, ct);
                _logger.LogDebug("Hardware device reconcile found {Count} present device(s)", devices.Count);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private static string ClassifyUsbDevice(string sysfsDir)
    {
        var bDeviceClass = ReadSysFile(Path.Combine(sysfsDir, "bDeviceClass")) ?? string.Empty;
        return bDeviceClass.Trim() switch
        {
            "08" => "storage",   // Mass Storage
            "03" => "input",     // HID (keyboard/mouse/gamepad)
            "01" => "audio",     // Audio
            "0e" => "display",   // Video/imaging (webcams)
            "02" => "usb",       // Communications
            _ => "usb",
        };
    }

    private static string? ReadSysFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path).Trim();
        }
        catch { return null; }
    }

    /// <summary>Resolve /sys/... to its udev-style DEVPATH (e.g. /devices/pci0000:00/.../1-8).</summary>
    private static string ResolveSysfsPath(string sysfsEntry)
    {
        try
        {
            var info = new DirectoryInfo(sysfsEntry);
            var target = info.LinkTarget;
            if (!string.IsNullOrEmpty(target))
            {
                // /sys/bus/usb/devices/1-8 → ../../../devices/pci.../usb1/1-8 (relative).
                // Resolve against /sys and strip the /sys prefix so it matches the
                // absolute DEVPATH that udev emits in monitor events.
                var full = Path.GetFullPath(target, "/sys");
                if (full.StartsWith("/sys", StringComparison.Ordinal))
                    return full["/sys".Length..].TrimStart('/');
                return full.TrimEnd('/');
            }
            return info.FullName;
        }
        catch { return sysfsEntry; }
    }

    // ────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────

    private static bool CommandExists(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("which", name)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static Process? StartProcess(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            return Process.Start(psi);
        }
        catch { return null; }
    }
}
