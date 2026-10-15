using System.Collections.Concurrent;
using client.Configuration;
using client.Core.Dlp;
using client.Core.Models;
using Microsoft.Extensions.Logging;

namespace client.Services.Dlp;

/// <summary>Main-tracker facade: publish sensor events to the DLP agent over IPC.</summary>
public sealed class DlpEventPublisher
{
    private readonly DlpIpcPublisher _ipc;
    private readonly bool _enabled;
    private readonly ConcurrentDictionary<string, DateTime> _recentFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _recentUsb = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FileDedupWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UsbDedupWindow = TimeSpan.FromSeconds(2);

    public DlpEventPublisher(AppConfig config, ILogger<DlpIpcPublisher> logger)
    {
        _enabled = config.DlpEnabled;
        _ipc = new DlpIpcPublisher(config.DlpIpcName, logger);
    }

    public void PublishUsbPlugged(string vendor, string product, string deviceClass)
    {
        if (!_enabled) return;
        if (!IsUsbDataLossClass(deviceClass)) return;
        if (IsFailedDescriptor(product)) return;
        var subject = $"{vendor} {product}".Trim();
        if (string.IsNullOrWhiteSpace(subject)) return;
        if (!TryClaimUsbBurst("plugged", vendor, product)) return;

        _ipc.PublishFireAndForget(new DlpIpcEvent
        {
            Type = "usb_plugged",
            Subject = subject,
            Detail = deviceClass ?? "",
            At = DateTime.UtcNow,
        });
    }

    public void PublishUsbUnplugged(string vendor, string product, string deviceClass)
    {
        if (!_enabled) return;
        if (!IsUsbDataLossClass(deviceClass)) return;
        if (IsFailedDescriptor(product)) return;
        var subject = $"{vendor} {product}".Trim();
        if (string.IsNullOrWhiteSpace(subject)) return;
        if (!TryClaimUsbBurst("unplugged", vendor, product)) return;

        _ipc.PublishFireAndForget(new DlpIpcEvent
        {
            Type = "usb_unplugged",
            Subject = subject,
            Detail = deviceClass ?? "",
            At = DateTime.UtcNow,
        });
    }

    public void PublishFileOnRemovable(string path)
    {
        if (!_enabled) return;
        if (!DlpRemovablePath.IsOnRemovableVolume(path)) return;
        var now = DateTime.UtcNow;
        if (_recentFiles.TryGetValue(path, out var prev) && now - prev < FileDedupWindow)
            return;
        _recentFiles[path] = now;
        Prune(_recentFiles, now, FileDedupWindow);

        _ipc.PublishFireAndForget(new DlpIpcEvent
        {
            Type = "file_on_removable",
            Subject = path,
            Detail = Path.GetFileName(path) ?? "",
            At = now,
        });
    }

    /// <summary>
    /// Real cloud upload: user chose a file in a browser file-open dialog.
    /// Visit-only browsing must never call this.
    /// </summary>
    public void PublishCloudFileUpload(string url, string? domain, string? fileName)
    {
        if (!_enabled) return;
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(domain)) return;

        _ipc.PublishFireAndForget(new DlpIpcEvent
        {
            Type = "cloud_file_upload",
            Subject = string.IsNullOrWhiteSpace(url) ? (domain ?? "") : url,
            Detail = string.IsNullOrWhiteSpace(fileName)
                ? (domain ?? "")
                : $"{domain ?? ""}|{fileName}".Trim('|'),
            At = DateTime.UtcNow,
        });
    }

    private static bool IsUsbDataLossClass(string? deviceClass)
    {
        var c = (deviceClass ?? "").Trim().ToLowerInvariant();
        return c is "usb" or "storage";
    }

    private static bool IsFailedDescriptor(string? product)
    {
        var p = product ?? "";
        return p.Contains("Device Descriptor Request Failed", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Unknown USB Device", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One DLP event per plug/unplug burst — multi-PnP rows for one stick collapse.
    /// Uses a direction-scoped short window; any storage/usb event in that window wins once.
    /// </summary>
    private bool TryClaimUsbBurst(string direction, string vendor, string product)
    {
        var now = DateTime.UtcNow;
        // Global per-direction burst key so 3 PnP rows in the same second → 1 alert.
        var burstKey = direction;
        if (_recentUsb.TryGetValue(burstKey, out var prev) && now - prev < UsbDedupWindow)
            return false;
        _recentUsb[burstKey] = now;
        // Also stamp a product key for diagnostics / future refinement.
        var productKey = $"{direction}:{vendor}|{product}".ToLowerInvariant();
        _recentUsb[productKey] = now;
        Prune(_recentUsb, now, UsbDedupWindow);
        return true;
    }

    private static void Prune(ConcurrentDictionary<string, DateTime> map, DateTime now, TimeSpan window)
    {
        if (map.Count <= 64) return;
        foreach (var kv in map)
        {
            if (now - kv.Value > window)
                map.TryRemove(kv.Key, out _);
        }
    }
}
