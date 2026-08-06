using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace client.Core.BrowserAccessibility;

/// <summary>
/// Linux implementation of <see cref="IAccessibilityBrowserReader"/> using the AT-SPI
/// accessibility tree over D-Bus. Empirically validated against Chrome 151:
///   - browser windows are FRAME(23)/WINDOW(69) nodes under the app root;
///   - the omnibox is an ENTRY(79) node named "Address and search bar" whose Text
///     is the exact current URL/query (e.g. "google.com/search?q=...");
///   - the page title is the FRAME's Name property.
///
/// A short python3 script (embedded here — no extra files to bundle, installer parity)
/// walks the tree once per call and emits JSON. Headless instances (--headless) are
/// skipped; incognito windows are flagged via title/cmdline.
/// </summary>
public sealed class LinuxAtSpiBrowserReader : IAccessibilityBrowserReader
{
    private readonly ILogger<LinuxAtSpiBrowserReader> _logger;
    private bool? _checked;

    public string Platform => "Linux";
    public bool IsAvailable => OperatingSystem.IsLinux();

    public LinuxAtSpiBrowserReader(ILogger<LinuxAtSpiBrowserReader> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<AccessibilitySnapshot>> ReadAsync(CancellationToken ct)
    {
        var result = new List<AccessibilitySnapshot>();
        if (!OperatingSystem.IsLinux() || ct.IsCancellationRequested)
            return result;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = "-",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                LogUnavailable("python3 could not be started");
                return result;
            }

            await proc.StandardInput.WriteAsync(ProbeScript.AsMemory(), ct);
            proc.StandardInput.Close();

            var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errorTask = proc.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }

            var output = await outputTask;
            var err = await errorTask;

            if (string.IsNullOrWhiteSpace(output) || output.Trim() == "[]")
            {
                if (!string.IsNullOrWhiteSpace(err))
                    _logger.LogTrace("AT-SPI probe stderr: {Err}", err.Trim());
                if (_checked != true)
                {
                    LogUnavailable(string.IsNullOrWhiteSpace(err) ? "no browser windows visible via AT-SPI" : err.Trim());
                }
                return result;
            }

            _checked = true;
            using var doc = JsonDocument.Parse(output);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    result.Add(new AccessibilitySnapshot
                    {
                        WindowKey = el.GetProperty("key").GetString() ?? string.Empty,
                        ProcessId = el.GetProperty("pid").GetInt32(),
                        ProcessName = el.GetProperty("proc").GetString() ?? string.Empty,
                        WindowTitle = el.GetProperty("title").GetString() ?? string.Empty,
                        Url = el.GetProperty("url").GetString(),
                        IsIncognito = el.TryGetProperty("incognito", out var inc) && inc.GetBoolean(),
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Malformed AT-SPI probe entry");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AT-SPI browser read failed");
        }

        return result;
    }

    private void LogUnavailable(string reason)
    {
        _checked = true;
        _logger.LogInformation(
            "Accessibility browser reader (Linux/AT-SPI) unavailable: {Reason}. " +
            "Browser journeys will not be captured until the desktop accessibility daemon is reachable.",
            reason);
    }

    /// <summary>
    /// Embedded probe script (validated on Chrome 151). Prints a JSON array of
    /// browser windows: {{key, pid, proc, title, url, incognito}}.
    /// </summary>
    private const string ProbeScript = """
        import dbus, os, sys, json
        from collections import deque

        # ── connect to the AT-SPI bus ──
        try:
            raw = os.popen('gdbus call --session --dest org.a11y.Bus --object-path /org/a11y/bus --method org.a11y.Bus.GetAddress 2>/dev/null').read().strip()
            start = raw.find("'") + 1
            end = raw.rfind("'")
            addr = raw[start:end] if start > 0 and end > start else ''
            if not addr:
                print('[]'); sys.exit(0)
            bus = dbus.bus.BusConnection(addr)
        except Exception:
            print('[]'); sys.exit(0)

        A11Y = 'org.a11y.atspi.Accessible'
        PROPS = 'org.freedesktop.DBus.Properties'
        TEXT = 'org.a11y.atspi.Text'
        BROWSER_HINTS = ('chrome', 'chromium', 'firefox', 'brave', 'edge', 'msedge', 'vivaldi', 'opera', 'safari', 'arc', 'iexplore')

        def getp(obj, name):
            try:
                return obj.Get(A11Y, name, dbus_interface=PROPS)
            except Exception:
                return None

        def get_text(obj):
            try:
                return str(obj.GetText(0, 500, dbus_interface=TEXT))
            except Exception:
                return ''

        def url_of(t):
            t = (t or '').strip()
            if not t:
                return ''
            low = t.lower()
            if low.startswith(('http://', 'https://')):
                return t
            if '://' in low:
                return t
            if any(ch.isspace() for ch in t) or '.' not in t:
                return ''
            return 'https://' + t

        def find_address_bar(win):
            # Depth-first (toolbar subtree comes first in a11y order) — prefer the
            # omnibox node ("Address and search bar"), fall back to the first
            # editbar/entry whose text looks like a URL.
            addr = ''
            fallback = ''
            stack = [(win, 0)]
            seen = set()
            budget = 1200
            while stack and budget > 0:
                obj, depth = stack.pop()
                budget -= 1
                if depth > 10:
                    continue
                try:
                    kids = obj.GetChildren(dbus_interface=A11Y)
                except Exception:
                    continue
                for kb, kp in kids:
                    pk = str(kb) + str(kp)
                    if pk in seen:
                        continue
                    seen.add(pk)
                    try:
                        k = bus.get_object(str(kb), str(kp))
                        krole = int(k.GetRole(dbus_interface=A11Y))
                        kname = str(getp(k, 'Name') or '')
                    except Exception:
                        continue
                    if krole in (77, 79):  # EDITBAR / ENTRY
                        txt = get_text(k).strip()
                        if txt:
                            if 'address and search bar' in kname.lower() or 'search or enter address' in kname.lower():
                                addr = url_of(txt)
                                if addr:
                                    return addr
                                continue
                            if not fallback:
                                fallback = url_of(txt)
                    stack.append((k, depth + 1))
            return addr or fallback

        results = []
        try:
            registry = bus.get_object('org.a11y.atspi.Registry', '/org/a11y/atspi/accessible/root')
            children = registry.GetChildren(dbus_interface=A11Y)
        except Exception:
            print('[]'); sys.exit(0)

        dbus_obj = bus.get_object('org.freedesktop.DBus', '/org/freedesktop/DBus')
        for app_bus_name, _ in children:
            name = str(app_bus_name)
            try:
                pid = int(dbus_obj.GetConnectionUnixProcessID(name, dbus_interface='org.freedesktop.DBus'))
            except Exception:
                continue
            if pid <= 0:
                continue
            try:
                comm = open('/proc/%d/comm' % pid).read().strip().lower()
            except Exception:
                continue
            try:
                cmd = open('/proc/%d/cmdline' % pid).read().replace('\0', ' ').lower()
            except Exception:
                cmd = ''
            if '--headless' in cmd:
                continue
            if not any(h in comm for h in BROWSER_HINTS) and not any(h in cmd for h in BROWSER_HINTS):
                continue
            try:
                app_obj = bus.get_object(name, '/org/a11y/atspi/accessible/root')
                wins = app_obj.GetChildren(dbus_interface=A11Y)
            except Exception:
                continue
            for win_bus, win_path in wins:
                try:
                    w = bus.get_object(str(win_bus), str(win_path))
                    wrole = int(w.GetRole(dbus_interface=A11Y))
                    wname = str(getp(w, 'Name') or '')
                except Exception:
                    continue
                if wrole not in (23, 69):  # FRAME / WINDOW
                    continue
                if not wname.strip():
                    continue
                # Incognito is per-window: a single browser process hosts normal AND
                # incognito windows, so one incognito window must never flag its
                # siblings (that would strip their URLs when capture is gated off).
                win_incognito = '--incognito' in cmd
                if any(s in wname.lower() for s in ('incognito', 'inprivate', 'private browsing')):
                    win_incognito = True
                url = find_address_bar(w)
                results.append({
                    'key': name + '|' + str(win_path),
                    'pid': pid,
                    'proc': comm,
                    'title': wname,
                    'url': url,
                    'incognito': win_incognito,
                })

        print(json.dumps(results))
        """;
}
