using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using client.Configuration;
using client.Core.BrowserAccessibility;
using client.Core.Dlp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services.Dlp;

/// <summary>
/// Detects real browser file-upload intent via the OS file-open dialog, then
/// publishes <c>cloud_file_upload</c> with the foreground tab URL/domain.
/// Visit-only browsing never produces alerts. Drag/drop/paste are out of scope.
/// </summary>
public sealed class DlpBrowserUploadWatcher : BackgroundService
{
    private readonly AppConfig _config;
    private readonly DlpEventPublisher _dlp;
    private readonly IBrowserForegroundUrl _fgUrl;
    private readonly IBrowserRegistry _browsers;
    private readonly ILogger<DlpBrowserUploadWatcher> _logger;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PublishDedup = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FgUrlMaxAge = TimeSpan.FromMinutes(2);

    // hwnd → last filename seen in the dialog
    private readonly Dictionary<nint, string> _openDialogs = new();
    private string _lastPublishKey = "";
    private DateTime _lastPublishAt = DateTime.MinValue;

    public DlpBrowserUploadWatcher(
        AppConfig config,
        DlpEventPublisher dlp,
        IBrowserForegroundUrl fgUrl,
        IBrowserRegistry browsers,
        ILogger<DlpBrowserUploadWatcher> logger)
    {
        _config = config;
        _dlp = dlp;
        _fgUrl = fgUrl;
        _browsers = browsers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.DlpEnabled)
        {
            _logger.LogInformation("DlpBrowserUploadWatcher: DLP disabled — parked");
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation(
                "DlpBrowserUploadWatcher: upload detect deferred on this OS (Windows file-open dialog only)");
            return;
        }

        _logger.LogInformation("DlpBrowserUploadWatcher: watching browser file-open dialogs");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    PollWindowsDialogs();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DlpBrowserUploadWatcher: poll failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void PollWindowsDialogs()
    {
        if (!OperatingSystem.IsWindows()) return;

        var seen = new HashSet<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var cls = GetClassName(hwnd);
            if (!IsFileOpenDialogClass(cls)) return true;

            var title = GetWindowText(hwnd);
            if (!LooksLikeOpenDialog(title)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return true;
            if (!IsAttributedToBrowserUpload((int)pid, hwnd)) return true;

            seen.Add(hwnd);
            var fileName = ReadDialogFileName(hwnd);
            if (!string.IsNullOrWhiteSpace(fileName))
                _openDialogs[hwnd] = fileName.Trim();
            else if (!_openDialogs.ContainsKey(hwnd))
                _openDialogs[hwnd] = "";

            return true;
        }, IntPtr.Zero);

        foreach (var hwnd in _openDialogs.Keys.ToList())
        {
            if (seen.Contains(hwnd)) continue;
            var fileName = _openDialogs[hwnd];
            _openDialogs.Remove(hwnd);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                _logger.LogDebug("DlpBrowserUploadWatcher: open dialog closed with empty filename (cancel?)");
                continue;
            }

            if (!_fgUrl.TryGetFresh(FgUrlMaxAge, out var url, out var domain))
            {
                _logger.LogInformation(
                    "DlpBrowserUploadWatcher: file chosen ({File}) but no fresh browser tab URL — skipped",
                    fileName);
                continue;
            }

            var key = $"{domain}|{fileName}";
            var now = DateTime.UtcNow;
            if (string.Equals(key, _lastPublishKey, StringComparison.OrdinalIgnoreCase)
                && now - _lastPublishAt < PublishDedup)
                continue;

            _lastPublishKey = key;
            _lastPublishAt = now;
            _dlp.PublishCloudFileUpload(url, domain, fileName);
            _logger.LogInformation(
                "DlpBrowserUploadWatcher: cloud upload intent domain={Domain} file={File}",
                domain, fileName);
        }
    }

    /// <summary>
    /// Classic common dialog (#32770) plus modern WinUI / Shell file-picker hosts.
    /// </summary>
    private static bool IsFileOpenDialogClass(string cls)
    {
        if (string.IsNullOrWhiteSpace(cls)) return false;
        if (string.Equals(cls, "#32770", StringComparison.Ordinal)) return true;
        // Windows 10/11 modern pickers (OS hosts — not product names).
        if (cls.Contains("FilePicker", StringComparison.OrdinalIgnoreCase)) return true;
        if (cls.Contains("OpenFileDialog", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(cls, "CabinetWClass", StringComparison.OrdinalIgnoreCase)) return false; // Explorer windows
        return false;
    }

    private static bool LooksLikeOpenDialog(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var t = title.ToLowerInvariant();
        if (t.Contains("save as") || t.StartsWith("save ") || t.Contains("print"))
            return false;
        return true;
    }

    /// <summary>
    /// Chromium often hosts the picker in PickerHost/ApplicationFrameHost, not chrome.exe.
    /// Attribute via dialog PID, owner window PID, or fresh foreground browser URL + OS picker host.
    /// </summary>
    private bool IsAttributedToBrowserUpload(int dialogPid, nint dialogHwnd)
    {
        if (IsBrowserProcess(dialogPid))
            return true;

        var owner = GetWindow(dialogHwnd, GwOwner);
        if (owner != IntPtr.Zero)
        {
            GetWindowThreadProcessId(owner, out var ownerPid);
            if (ownerPid != 0 && IsBrowserProcess((int)ownerPid))
                return true;
        }

        var root = GetAncestor(dialogHwnd, GaRootOwner);
        if (root != IntPtr.Zero && root != dialogHwnd)
        {
            GetWindowThreadProcessId(root, out var rootPid);
            if (rootPid != 0 && IsBrowserProcess((int)rootPid))
                return true;
        }

        // Modern OS file picker while a browser tab URL is fresh (Claude/DeepSeek paperclip).
        if (IsOsFilePickerHost(dialogPid)
            && _fgUrl.TryGetFresh(FgUrlMaxAge, out _, out _))
            return true;

        return false;
    }

    private bool IsBrowserProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName ?? "";
            if (_browsers.IsBrowser(name))
                return true;
            if (name.Contains("msedgewebview", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>OS file-picker host processes (not user-installed product names).</summary>
    private static bool IsOsFilePickerHost(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = (p.ProcessName ?? "").ToLowerInvariant();
            return name is "pickerhost" or "applicationframehost" or "openwith"
                or "explorer" or "sihost" or "shellexperiencehost";
        }
        catch
        {
            return false;
        }
    }

    private static string ReadDialogFileName(nint dialogHwnd)
    {
        // Classic Open: ComboBoxEx32 → ComboBox → Edit
        nint edit = FindWindowEx(dialogHwnd, IntPtr.Zero, "ComboBoxEx32", null);
        if (edit != IntPtr.Zero)
        {
            var combo = FindWindowEx(edit, IntPtr.Zero, "ComboBox", null);
            if (combo != IntPtr.Zero)
            {
                var inner = FindWindowEx(combo, IntPtr.Zero, "Edit", null);
                if (inner != IntPtr.Zero)
                {
                    var t = GetWindowText(inner);
                    if (!string.IsNullOrWhiteSpace(t)) return t;
                }
            }
        }

        edit = FindWindowEx(dialogHwnd, IntPtr.Zero, "Edit", null);
        if (edit != IntPtr.Zero)
        {
            var t = GetWindowText(edit);
            if (!string.IsNullOrWhiteSpace(t)) return t;
        }

        // Walk all descendants for a non-empty Edit (Chrome / WinUI variants).
        string best = "";
        EnumChildWindows(dialogHwnd, (child, _) =>
        {
            var cls = GetClassName(child);
            if (!string.Equals(cls, "Edit", StringComparison.OrdinalIgnoreCase)
                && !cls.Contains("Edit", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(cls, "TextBox", StringComparison.OrdinalIgnoreCase))
                return true;
            var text = GetWindowText(child);
            if (string.IsNullOrWhiteSpace(text)) return true;
            // Prefer paths / names with an extension over tiny spinner labels.
            if (text.Length > best.Length)
                best = text;
            return true;
        }, IntPtr.Zero);

        return best;
    }

    private static string GetClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWindowText(nint hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private const uint GwOwner = 4;
    private const uint GaRootOwner = 3;

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint hwndParent, nint hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hWnd, uint gaFlags);
}
