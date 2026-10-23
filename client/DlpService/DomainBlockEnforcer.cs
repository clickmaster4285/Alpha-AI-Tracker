using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlphaAITracker.Service;

/// <summary>
/// P2 web domain block (Windows):
/// 1) hosts DNS sink for A + AAAA (<c>0.0.0.0</c> and <c>::</c>) with <c># AlphaAI-DLP</c> markers
/// 2) outbound firewall rules <c>AlphaAI-DLP-*</c> for real IPs (DoH / browser bypass of hosts)
/// Requires LocalSystem. Call <see cref="FlushDns"/> after apply.
/// </summary>
internal static class DomainBlockEnforcer
{
    public const string MarkerPrefix = "# AlphaAI-DLP";
    public const string FirewallRulePrefix = "AlphaAI-DLP-";
    private const string BeginMarker = "# AlphaAI-DLP-BEGIN";
    private const string EndMarker = "# AlphaAI-DLP-END";

    private static readonly HttpClient DnsHttp = CreateDnsClient();

    public sealed record Result(
        bool Changed,
        int DesiredCount,
        int AppliedCount,
        string Detail);

    public static string HostsPath
    {
        get
        {
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return Path.Combine(windir, "System32", "drivers", "etc", "hosts");
        }
    }

    public static Result Apply(IReadOnlyList<string> domains)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, domains.Count, 0, "not_windows");

        var desired = domains
            .SelectMany(PolicyBundleParser.NormalizeDomain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hostsResult = ApplyHosts(desired);
        var fwResult = ApplyFirewall(desired);
        FlushDns();

        var changed = hostsResult.Changed || fwResult.Changed;
        var detail = $"hosts={hostsResult.Detail}; fw={fwResult.Detail}";
        if (hostsResult.Detail.StartsWith("access_denied", StringComparison.Ordinal) ||
            fwResult.Detail.StartsWith("access_denied", StringComparison.Ordinal))
            detail = "access_denied_need_admin";

        return new Result(changed, desired.Count, desired.Count, detail);
    }

    public static Result Clear() => Apply(Array.Empty<string>());

    private static Result ApplyHosts(IReadOnlyList<string> desired)
    {
        string original;
        try
        {
            original = File.Exists(HostsPath)
                ? File.ReadAllText(HostsPath, Encoding.UTF8)
                : "";
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new Result(false, desired.Count, 0, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, desired.Count, 0, "error_read:" + ex.Message);
        }

        var stripped = StripOwnedBlock(original);
        var next = stripped.TrimEnd() + "\r\n";
        if (desired.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(BeginMarker);
            foreach (var d in desired)
            {
                // IPv4 + IPv6 — A-only sink leaves Happy Eyeballs / AAAA working.
                sb.Append("0.0.0.0 ").Append(d).Append(' ').AppendLine(MarkerPrefix);
                sb.Append(":: ").Append(d).Append(' ').AppendLine(MarkerPrefix);
            }
            sb.AppendLine(EndMarker);
            next += sb.ToString();
        }

        if (NormalizeNewlines(next) == NormalizeNewlines(original))
            return new Result(false, desired.Count, desired.Count, "already_applied");

        try
        {
            var tmp = HostsPath + ".alpha-dlp.tmp";
            File.WriteAllText(tmp, next, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Copy(tmp, HostsPath, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
            return new Result(true, desired.Count, desired.Count,
                desired.Count == 0 ? "hosts_cleared" : "hosts_updated");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new Result(false, desired.Count, 0, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, desired.Count, 0, "error_write:" + ex.Message);
        }
    }

    private static Result ApplyFirewall(IReadOnlyList<string> desired)
    {
        try
        {
            var existing = ListOwnedFirewallRules();
            foreach (var name in existing)
                RunNetsh($"advfirewall firewall delete rule name=\"{EscapeNetsh(name)}\"");

            if (desired.Count == 0)
                return new Result(existing.Count > 0, 0, 0, existing.Count > 0 ? "fw_cleared" : "fw_empty");

            var added = 0;
            foreach (var domain in desired)
            {
                // Skip www. twin — resolve apex only once when both present.
                if (domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    var apex = domain[4..];
                    if (desired.Contains(apex, StringComparer.OrdinalIgnoreCase))
                        continue;
                }

                var ips = ResolvePublicIps(domain);
                if (ips.Count == 0)
                    continue;

                var ruleName = FirewallRulePrefix + SanitizeRuleName(domain);
                var remote = string.Join(",", ips);
                var args =
                    $"advfirewall firewall add rule name=\"{EscapeNetsh(ruleName)}\" " +
                    $"dir=out action=block protocol=any remoteip={remote} " +
                    $"enable=yes profile=any";
                var (ok, _) = RunNetsh(args);
                if (ok) added++;
            }

            return new Result(true, desired.Count, added, $"fw_rules={added}");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new Result(false, desired.Count, 0, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, desired.Count, 0, "fw_error:" + ex.Message);
        }
    }

    /// <summary>
    /// Resolve A/AAAA via Cloudflare DNS-over-HTTPS JSON so we get real IPs even when
    /// local hosts already sinks the name (needed for firewall rules vs browser DoH).
    /// </summary>
    internal static IReadOnlyList<string> ResolvePublicIps(string domain)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var type in new[] { "A", "AAAA" })
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(domain)}&type={type}");
                req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
                using var resp = DnsHttp.Send(req);
                if (!resp.IsSuccessStatusCode)
                    continue;
                using var stream = resp.Content.ReadAsStream();
                using var doc = JsonDocument.Parse(stream);
                if (!doc.RootElement.TryGetProperty("Answer", out var answers) ||
                    answers.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var a in answers.EnumerateArray())
                {
                    if (!a.TryGetProperty("data", out var data))
                        continue;
                    var ip = data.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(ip))
                        continue;
                    if (IPAddress.TryParse(ip, out var addr) &&
                        addr.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 &&
                        !IPAddress.IsLoopback(addr) &&
                        !ip.Equals("0.0.0.0", StringComparison.Ordinal))
                    {
                        set.Add(addr.ToString());
                    }
                }
            }
        }
        catch
        {
            // Fallback: system DNS (may be 0.0.0.0 if hosts already applied).
            try
            {
                foreach (var addr in Dns.GetHostAddresses(domain))
                {
                    if (addr.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 &&
                        !IPAddress.IsLoopback(addr) &&
                        !addr.Equals(IPAddress.Any) &&
                        !addr.Equals(IPAddress.IPv6Any))
                        set.Add(addr.ToString());
                }
            }
            catch { /* ignore */ }
        }

        return set.ToList();
    }

    private static List<string> ListOwnedFirewallRules()
    {
        var list = new List<string>();
        var (ok, output) = RunNetsh("advfirewall firewall show rule name=all");
        if (!ok || string.IsNullOrEmpty(output))
            return list;

        // Rules are blocks; "Rule Name:" lines.
        foreach (Match m in Regex.Matches(output, @"^Rule Name:\s*(.+)\s*$", RegexOptions.Multiline))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.StartsWith(FirewallRulePrefix, StringComparison.OrdinalIgnoreCase))
                list.Add(name);
        }
        return list;
    }

    private static (bool Ok, string Output) RunNetsh(string args)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            p.Start();
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);
            return (p.ExitCode == 0, stdout + stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static void FlushDns()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(10_000);
        }
        catch { /* ignore */ }
    }

    internal static string StripOwnedBlock(string content)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        var inBlock = false;
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Equals(BeginMarker, StringComparison.OrdinalIgnoreCase))
            {
                inBlock = true;
                continue;
            }
            if (t.Equals(EndMarker, StringComparison.OrdinalIgnoreCase))
            {
                inBlock = false;
                continue;
            }
            if (inBlock)
                continue;
            if (t.Contains(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(line);
        }
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1]))
            kept.RemoveAt(kept.Count - 1);
        return string.Join("\r\n", kept);
    }

    private static string SanitizeRuleName(string domain)
    {
        var s = Regex.Replace(domain, @"[^a-zA-Z0-9.\-_]", "_");
        if (s.Length > 40) s = s[..40];
        return s;
    }

    private static string EscapeNetsh(string s) => s.Replace("\"", "'");

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd() + "\n";

    private static HttpClient CreateDnsClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AlphaAITracker-DLP/1");
        return c;
    }

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is System.Security.SecurityException
        || ex.Message.Contains("access is not allowed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);
}
