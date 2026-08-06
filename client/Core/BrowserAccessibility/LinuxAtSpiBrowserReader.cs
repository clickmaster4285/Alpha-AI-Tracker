using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace client.Core.BrowserAccessibility;

/// <summary>
/// Linux implementation of <see cref="IAccessibilityBrowserReader"/>.
///
/// ONE embedded python3 probe produces every window that is visible to the OS, from
/// three independent sources merged into a single snapshot list:
///
///   A) AT-SPI accessibility tree (D-Bus) — the source for a11y-visible browsers
///      (Chrome, Chromium, Brave, Edge, … on Wayland AND X11). Yields the exact
///      omnibox URL when the browser exposes it (older Chrome; Firefox on X11)
///      plus the page title and an incognito flag.
///   B) Firefox sessionstore files (recovery.jsonlz4 / sessionstore.jsonlz4) —
///      the ONLY source that survives snap Firefox's AppArmor sandbox (it blocks
///      every inbound D-Bus call, so AT-SPI cannot see the window). Firefox writes
///      its open windows/tabs (title + exact URL, including the active tab) into
///      its own profile every ~15 s; the file is plain-readable. Decompressed
///      with an embedded pure-python LZ4 block decoder (no external module).
///   C) The window-manager window list (GNOME Shell Introspect where permitted,
///      otherwise xprop _NET_CLIENT_LIST) — gives every window a STABLE id
///      (X11/XWayland windows; Shell ids on supported desktops), which fixes the
///      AT-SPI registry-path churn that fragmented journeys, and surfaces
///      X11-only browsers that AT-SPI cannot see.
///
/// Windows that only the WM sees (title-only) are emitted anyway — the
/// <see cref="BrowserHistoryReader"/> fallback resolves their URL later. The
/// tracker never sees an a11y-invisible browser disappear again.
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

        var now = DateTime.UtcNow;

        // ── Source C: WM window list (fast; stable ids; also covers X11-only browsers) ──
        var wmWindows = EnumerateWmWindows();

        // ── Sources A + B: one python3 probe (AT-SPI + Firefox sessionstore) ──
        List<ProbeWindow> probe = new();
        try
        {
            probe = await RunCombinedProbeAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Combined browser probe failed");
        }

        var wmIndexed = IndexWmWindows(wmWindows);

        // 1. Emit AT-SPI windows first — they carry URL + incognito. Their identity is
        //    upgraded to the WM id when a WM window matches (stable across navigation);
        //    otherwise the a11y key is kept (best effort — the tracker re-keys on churn).
        foreach (var w in probe)
        {
            if (w.Source != "a11y")
                continue;
            if (string.IsNullOrWhiteSpace(w.Title) && string.IsNullOrWhiteSpace(w.Url))
                continue;

            var wm = wmIndexed.FirstOrDefault(x => x.Pid == w.Pid && TitlesOverlap(x.Title, w.Title));
            var key = wm is not null ? "wm:" + wm.Id : "a11y:" + w.Key;

            result.Add(new AccessibilitySnapshot
            {
                WindowKey = key,
                ProcessId = w.Pid,
                ProcessName = w.ProcessName,
                WindowTitle = w.Title,
                Url = string.IsNullOrWhiteSpace(w.Url) ? null : BrowserAccessibilityHelpers.NormalizeUrl(w.Url),
                UrlSource = "accessibility",
                IsIncognito = w.Incognito,
                CapturedAt = now,
            });
        }

        // 2. WM windows that are browsers but invisible to AT-SPI (X11-only browsers,
        //    a11y-less setups) → title-only snapshots; history fills the URL later.
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in result)
            seenTitles.Add($"{s.ProcessId}|{BrowserAccessibilityHelpers.StripBrowserSuffix(s.WindowTitle)}");

        foreach (var wm in wmWindows)
        {
            if (string.IsNullOrWhiteSpace(wm.Title)) continue;
            var processName = ReadComm(wm.Pid);
            if (!BrowserAccessibilityHelpers.IsBrowserProcess(processName))
                continue;

            var t = BrowserAccessibilityHelpers.StripBrowserSuffix(wm.Title);
            if (seenTitles.Contains($"{wm.Pid}|{t}")) continue;

            result.Add(new AccessibilitySnapshot
            {
                WindowKey = "wm:" + wm.Id,
                ProcessId = wm.Pid,
                ProcessName = processName,
                WindowTitle = wm.Title,
                Url = null,
                UrlSource = "history",
                IsIncognito = BrowserAccessibilityHelpers.TitleSuggestsIncognito(wm.Title),
                CapturedAt = now,
            });
            seenTitles.Add($"{wm.Pid}|{t}");
        }

        // 3. Firefox sessionstore windows — exact URL. Reuse an existing snapshot for the
        //    same window (prefer its stable key) and just fill the URL, else add fresh.
        foreach (var w in probe)
        {
            if (w.Source != "ff")
                continue;
            if (string.IsNullOrWhiteSpace(w.Title) && string.IsNullOrWhiteSpace(w.Url))
                continue;

            var existing = result.FirstOrDefault(s =>
                s.ProcessId == w.Pid && TitlesOverlap(s.WindowTitle, w.Title));
            if (existing is not null)
            {
                if (string.IsNullOrEmpty(existing.Url) && !string.IsNullOrWhiteSpace(w.Url))
                {
                    var idx = result.IndexOf(existing);
                    result[idx] = new AccessibilitySnapshot
                    {
                        WindowKey = existing.WindowKey,
                        ProcessId = existing.ProcessId,
                        ProcessName = existing.ProcessName,
                        WindowTitle = existing.WindowTitle,
                        Url = BrowserAccessibilityHelpers.NormalizeUrl(w.Url),
                        UrlSource = "sessionstore",
                        IsIncognito = existing.IsIncognito,
                        CapturedAt = now,
                    };
                }
                continue;
            }

            result.Add(new AccessibilitySnapshot
            {
                WindowKey = "ff:" + w.Key,
                ProcessId = w.Pid,
                ProcessName = w.ProcessName,
                WindowTitle = w.Title,
                Url = BrowserAccessibilityHelpers.NormalizeUrl(w.Url),
                UrlSource = "sessionstore",
                IsIncognito = w.Incognito,
                CapturedAt = now,
            });
        }

        // Drop exact key duplicates (an a11y window may also surface via the WM list).
        var byKey = new Dictionary<string, AccessibilitySnapshot>(StringComparer.Ordinal);
        foreach (var s in result)
        {
            if (!byKey.TryGetValue(s.WindowKey, out var prev) || !string.IsNullOrEmpty(s.Url) && string.IsNullOrEmpty(prev.Url))
                byKey[s.WindowKey] = s;
        }

        if (byKey.Count == 0)
        {
            if (_checked != true)
                LogUnavailable("no browser windows visible (AT-SPI, Firefox sessionstore, and the WM window list are all empty)");
            return Array.Empty<AccessibilitySnapshot>();
        }

        _checked = true;
        return byKey.Values.ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // WM window list (Source C)
    // ─────────────────────────────────────────────────────────────────────────────

    private sealed record WmWindow(string Id, int Pid, string Title);

    private List<WmWindow> EnumerateWmWindows()
    {
        var windows = new List<WmWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 1. GNOME Shell introspection — stable per-window ids, works on Wayland.
        //    (Denied on some setups — fall through to xprop.)
        try
        {
            var raw = Run("gdbus",
                "call --session --dest org.gnome.Shell --object-path /org/gnome/Shell/Introspect --method org.gnome.Shell.Introspect.GetWindows",
                5000);
            if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ShellIdRegex.Matches(raw);
                var pids = ShellPidRegex.Matches(raw);
                var titles = ShellTitleRegex.Matches(raw);
                var count = Math.Min(ids.Count, Math.Min(pids.Count, titles.Count));
                for (var i = 0; i < count; i++)
                {
                    var id = ids[i].Groups[1].Value.Trim('\'', '"');
                    if (!int.TryParse(pids[i].Groups[1].Value, out var pid) || pid <= 0) continue;
                    var title = titles[i].Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    if (seen.Add("wm:" + id))
                        windows.Add(new WmWindow(id, pid, title));
                }
                if (windows.Count > 0) return windows;
            }
        }
        catch { }

        // 2. X11/XWayland: xprop _NET_CLIENT_LIST → _NET_WM_PID + _NET_WM_NAME.
        try
        {
            var raw = Run("xprop", "-root -notype _NET_CLIENT_LIST", 2000);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (Match m in WindowIdRegex.Matches(raw))
                {
                    var wid = m.Value;
                    if (wid == "0x0") continue;

                    var pidRaw = Run("xprop", $"-id {wid} _NET_WM_PID", 2000);
                    var pidMatch = PidRegex.Match(pidRaw ?? string.Empty);
                    if (!pidMatch.Success || !int.TryParse(pidMatch.Groups[1].Value, out var pid) || pid <= 0)
                        continue;

                    var titleRaw = Run("xprop", $"-id {wid} _NET_WM_NAME", 2000);
                    var title = ExtractXpropString(titleRaw);
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    if (seen.Add("x11:" + wid))
                        windows.Add(new WmWindow(wid, pid, title));
                }
            }
        }
        catch { }

        return windows;
    }

    private static List<WmWindow> IndexWmWindows(List<WmWindow> windows)
    {
        var dedup = new List<WmWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in windows)
        {
            var t = BrowserAccessibilityHelpers.StripBrowserSuffix(w.Title);
            if (seen.Add($"{w.Pid}|{t}"))
                dedup.Add(w);
        }
        return dedup;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Combined python3 probe (Sources A + B)
    // ─────────────────────────────────────────────────────────────────────────────

    private sealed record ProbeWindow(string Source, string Key, int Pid, string ProcessName, string Title, string Url, bool Incognito);

    private async Task<List<ProbeWindow>> RunCombinedProbeAsync(CancellationToken ct)
    {
        var result = new List<ProbeWindow>();
        if (!OperatingSystem.IsLinux() || ct.IsCancellationRequested)
            return result;

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
        cts.CancelAfter(TimeSpan.FromSeconds(12));
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
                _logger.LogTrace("Combined probe stderr: {Err}", err.Trim());
            return result;
        }

        using var doc = JsonDocument.Parse(output);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                result.Add(new ProbeWindow(
                    el.GetProperty("src").GetString() ?? string.Empty,
                    el.GetProperty("key").GetString() ?? string.Empty,
                    el.GetProperty("pid").GetInt32(),
                    el.GetProperty("proc").GetString() ?? string.Empty,
                    el.GetProperty("title").GetString() ?? string.Empty,
                    el.GetProperty("url").GetString() ?? string.Empty,
                    el.TryGetProperty("incognito", out var inc) && inc.GetBoolean()));
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Malformed probe entry");
            }
        }
        return result;
    }

    private void LogUnavailable(string reason)
    {
        _checked = true;
        _logger.LogInformation(
            "Accessibility browser reader (Linux) unavailable: {Reason}. " +
            "Browser journeys will not be captured until a window source is reachable.",
            reason);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Embedded probe script. Prints a JSON array:
    //   [{"src":"a11y",key,pid,proc,title,url,incognito}, {"src":"ff",key,pid,proc,title,url,incognito}]
    // The Firefox branch reads sessionstore-backups/recovery.jsonlz4 (fresh, ~15s) or
    // sessionstore.jsonlz4, decompresses mozLz4 with a pure-python LZ4 block decoder,
    // and reports every open window's ACTIVE tab (title + exact URL). This is the only
    // window source that works for snap Firefox (AppArmor blocks AT-SPI D-Bus).
    // ─────────────────────────────────────────────────────────────────────────────

    private const string ProbeScript = """
        import os, sys, json, glob, struct
        try:
            import dbus
        except Exception:
            # python3 without the dbus module: the AT-SPI source is unavailable, but the
            # Firefox sessionstore source below must keep working (it needs no dbus).
            dbus = None

        # ── shared helpers ──
        def read_comm(pid):
            try:
                return open('/proc/%d/comm' % pid).read().strip().lower()
            except Exception:
                return ''

        def read_cmd(pid):
            try:
                return open('/proc/%d/cmdline' % pid).read().replace('\0', ' ').lower()
            except Exception:
                return ''

        results = []

        # ═══════════════════════════════════════════════════════════════════
        # SOURCE A — AT-SPI accessibility tree
        # ═══════════════════════════════════════════════════════════════════
        try:
            raw = os.popen('gdbus call --session --dest org.a11y.Bus --object-path /org/a11y/bus --method org.a11y.Bus.GetAddress 2>/dev/null').read().strip()
            start = raw.find("'") + 1
            end = raw.rfind("'")
            addr = raw[start:end] if start > 0 and end > start else ''
            if addr:
                bus = dbus.bus.BusConnection(addr)

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

                registry = bus.get_object('org.a11y.atspi.Registry', '/org/a11y/atspi/accessible/root')
                children = registry.GetChildren(dbus_interface=A11Y)
                dbus_obj = bus.get_object('org.freedesktop.DBus', '/org/freedesktop/DBus')
                for app_bus_name, _ in children:
                    name = str(app_bus_name)
                    try:
                        pid = int(dbus_obj.GetConnectionUnixProcessID(name, dbus_interface='org.freedesktop.DBus'))
                    except Exception:
                        continue
                    if pid <= 0:
                        continue
                    comm = read_comm(pid)
                    cmd = read_cmd(pid)
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
                            'src': 'a11y',
                            'key': name + '|' + str(win_path),
                            'pid': pid,
                            'proc': comm,
                            'title': wname,
                            'url': url,
                            'incognito': win_incognito,
                        })
        except Exception:
            pass

        # ═══════════════════════════════════════════════════════════════════
        # SOURCE B — Firefox sessionstore (survives snap AppArmor; exact URLs)
        # ═══════════════════════════════════════════════════════════════════
        def lz4_decompress(src):
            # Pure-python LZ4 block decompressor (mozLz4 payload).
            out = bytearray()
            pos = 0
            n = len(src)
            try:
                while pos < n:
                    token = src[pos]; pos += 1
                    lit_len = token >> 4
                    if lit_len == 15:
                        while True:
                            b = src[pos]; pos += 1
                            lit_len += b
                            if b != 255:
                                break
                    if pos + lit_len > n:
                        return None
                    out += src[pos:pos + lit_len]; pos += lit_len
                    if pos >= n:
                        break
                    if pos + 2 > n:
                        return None
                    offset = src[pos] | (src[pos + 1] << 8); pos += 2
                    if offset == 0 or offset > len(out):
                        return None
                    match_len = (token & 0x0F) + 4
                    if (token & 0x0F) == 15:
                        while True:
                            b = src[pos]; pos += 1
                            match_len += b
                            if b != 255:
                                break
                    start = len(out) - offset
                    for i in range(match_len):
                        out.append(out[start + i])
            except Exception:
                return None
            return bytes(out)

        def read_mozlz4(path):
            # mozLz4 layout: 8-byte "mozLz40\0" header + 4-byte LE uncompressed size
            # + one LZ4 block (mozilla::Compression::LZ4 writes the size prefix).
            try:
                with open(path, 'rb') as f:
                    data = f.read()
                if data[:8] != b'mozLz40\0':
                    return None
                size = struct.unpack('<I', data[8:12])[0]
                raw = lz4_decompress(data[12:])
                if raw is None or len(raw) != size:
                    return None
                return raw
            except Exception:
                return None

        def active_tab(w):
            try:
                tabs = w.get('tabs') or []
                if not tabs:
                    return None
                idx = int(w.get('selected', 1)) - 1
                if idx < 0 or idx >= len(tabs):
                    idx = 0
                tab = tabs[idx]
                entries = tab.get('entries') or []
                eidx = int(tab.get('index', 1)) - 1
                if eidx < 0 or eidx >= len(entries):
                    eidx = len(entries) - 1
                if eidx < 0:
                    return None
                e = entries[eidx]
                url = (e.get('url') or '').strip()
                title = (e.get('title') or '').strip() or title_from_url(url)
                return (url, title)
            except Exception:
                return None

        def title_from_url(url):
            try:
                from urllib.parse import urlparse
                host = urlparse(url).netloc
                return host or url
            except Exception:
                return url

        try:
            home = os.path.expanduser('~')
            ff_roots = [
                os.path.join(home, '.mozilla', 'firefox'),
                os.path.join(home, 'snap', 'firefox', 'common', '.mozilla', 'firefox'),
            ]
            # A running Firefox main process? (sessionstore may linger after exit)
            ff_pid = 0
            for pid in os.listdir('/proc'):
                if not pid.isdigit():
                    continue
                if read_comm(int(pid)) == 'firefox':
                    ff_pid = int(pid)
                    break

            if ff_pid > 0:
                seen_wins = set()
                for root in ff_roots:
                    if not os.path.isdir(root):
                        continue
                    for profile in sorted(os.listdir(root)):
                        pdir = os.path.join(root, profile)
                        if not os.path.isdir(pdir) or profile.startswith('.'):
                            continue
                        candidates = []
                        r = os.path.join(pdir, 'sessionstore-backups', 'recovery.jsonlz4')
                        if os.path.exists(r):
                            candidates.append(r)
                        s = os.path.join(pdir, 'sessionstore.jsonlz4')
                        if os.path.exists(s):
                            candidates.append(s)
                        for path in candidates:
                            try:
                                raw = read_mozlz4(path)
                            except Exception:
                                raw = None
                            if raw is None:
                                continue
                            try:
                                data = json.loads(raw.decode('utf-8', 'replace'))
                            except Exception:
                                continue
                            windows = data.get('windows') or []
                            for i, w in enumerate(windows):
                                # Private windows are normally excluded from sessionstore by
                                # Firefox itself, but when they ARE present (some configs /
                                # future versions) they must flow through: the C# tracker
                                # gates their URL on ALPHA_BROWSER_CAPTURE_INCOGNITO, so this
                                # source is never a silent privacy leak — it is config-driven.
                                ispriv = bool(w.get('isPrivate'))
                                tab = active_tab(w)
                                if not tab:
                                    continue
                                url, title = tab
                                if not url or url.startswith(('about:', 'chrome:', 'moz-extension:')):
                                    continue
                                win_key = profile + '|' + str(i)
                                if win_key in seen_wins:
                                    continue
                                seen_wins.add(win_key)
                                results.append({
                                    'src': 'ff',
                                    'key': win_key,
                                    'pid': ff_pid,
                                    'proc': 'firefox',
                                    'title': title,
                                    'url': url,
                                    'incognito': ispriv,
                                })
                            break  # first readable sessionstore wins
        except Exception:
            pass

        print(json.dumps(results))
        """;

    // ─────────────────────────────────────────────────────────────────────────────
    // Shell helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static string? Run(string file, string args, int timeoutMs = 3000)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(timeoutMs);
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadComm(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/comm").Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? ExtractXpropString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = XpropNameRegex.Match(raw);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>True when two window titles refer to the same page (suffixes differ by
    /// browser flavor, quotes, or the a11y tree exposes one without the suffix).</summary>
    private static bool TitlesOverlap(string a, string b)
    {
        var x = BrowserAccessibilityHelpers.StripBrowserSuffix(a).Trim();
        var y = BrowserAccessibilityHelpers.StripBrowserSuffix(b).Trim();
        if (x.Length == 0 || y.Length == 0) return false;
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
        return x.Contains(y, StringComparison.OrdinalIgnoreCase) ||
               y.Contains(x, StringComparison.OrdinalIgnoreCase);
    }

    // Static regexes — NOT source-generated, so this compiles in every toolchain
    // (some IDEs/SDKs do not run the System.Text.RegularExpressions.Generator and
    // would otherwise fail with CS8795 on the partial declarations).
    private static readonly Regex ShellIdRegex = new(@"'id':\s*<\s*'?([^'>,]+)'?>");
    private static readonly Regex ShellPidRegex = new(@"wm-pid'?:\s*<int32\s+(\d+)");
    private static readonly Regex ShellTitleRegex = new(@"'title':\s*<\s*'([^']*)'");
    private static readonly Regex WindowIdRegex = new(@"0x[0-9a-f]+", RegexOptions.IgnoreCase);
    private static readonly Regex PidRegex = new(@"=\s*(\d+)");
    private static readonly Regex XpropNameRegex = new(@"=\s*""(.+?)""");
}
