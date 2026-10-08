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
    private static readonly TimeSpan FileDedupWindow = TimeSpan.FromSeconds(2);

    public DlpEventPublisher(AppConfig config, ILogger<DlpIpcPublisher> logger)
    {
        _enabled = config.DlpEnabled;
        _ipc = new DlpIpcPublisher(config.DlpIpcName, logger);
    }

    public void PublishUsbPlugged(string vendor, string product, string deviceClass)
    {
        if (!_enabled) return;
        // Prefer storage/usb class plugs; still publish all plugs — rule pattern filters.
        _ipc.PublishFireAndForget(new DlpIpcEvent
        {
            Type = "usb_plugged",
            Subject = $"{vendor} {product}".Trim(),
            Detail = deviceClass ?? "",
            At = DateTime.UtcNow,
        });
    }

    public void PublishFileOnRemovable(string path)
    {
        if (!_enabled) return;
        if (!DlpRemovablePath.IsOnRemovableVolume(path)) return;
        // created + changed often fire for one copy — collapse within a short window.
        var now = DateTime.UtcNow;
        if (_recentFiles.TryGetValue(path, out var prev) && now - prev < FileDedupWindow)
            return;
        _recentFiles[path] = now;
        if (_recentFiles.Count > 256)
        {
            foreach (var kv in _recentFiles)
            {
                if (now - kv.Value > FileDedupWindow)
                    _recentFiles.TryRemove(kv.Key, out _);
            }
        }
        _ipc.PublishFireAndForget(new DlpIpcEvent
        {
            Type = "file_on_removable",
            Subject = path,
            Detail = Path.GetFileName(path) ?? "",
            At = now,
        });
    }

    public void PublishBrowserUrl(string url, string? domain = null)
    {
        if (!_enabled) return;
        if (string.IsNullOrWhiteSpace(url)) return;
        _ipc.PublishFireAndForget(new DlpIpcEvent
        {
            Type = "browser_url",
            Subject = url,
            Detail = domain ?? "",
            At = DateTime.UtcNow,
        });
    }
}
