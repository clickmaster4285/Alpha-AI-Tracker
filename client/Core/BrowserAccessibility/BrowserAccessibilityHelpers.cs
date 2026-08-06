namespace client.Core.BrowserAccessibility;

/// <summary>
/// Shared helpers for the accessibility-based browser journey readers.
/// </summary>
public static class BrowserAccessibilityHelpers
{
    /// <summary>
    /// Process-name hints used to decide whether a window belongs to a web browser.
    /// Matched case-insensitively against the process name (comm) and command line.
    /// </summary>
    public static readonly string[] BrowserProcessHints =
    {
        "chrome", "chromium", "firefox", "brave", "edge", "msedge",
        "vivaldi", "opera", "safari", "arc", "microsoft-edge", "iexplore",
    };

    /// <summary>True if the process name/command line looks like a browser.</summary>
    public static bool IsBrowserProcess(string processName, string? commandLine = null)
    {
        var name = processName ?? string.Empty;
        if (BrowserProcessHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (!string.IsNullOrWhiteSpace(commandLine))
            return BrowserProcessHints.Any(h => commandLine.Contains(h, StringComparison.OrdinalIgnoreCase));
        return false;
    }

    /// <summary>
    /// Normalize the text read from a browser address bar into a full URL.
    /// Browsers display the omnibox without the scheme (e.g. "google.com/search?q=x"
    /// or "www.youtube.com/watch?v=abc"); we prepend https:// when the text looks
    /// like a host/path. Non-URL text (e.g. the user mid-typing keywords) is dropped.
    /// </summary>
    public static string NormalizeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var t = raw.Trim().TrimStart('\u200b', '\ufeff', '"', ' ');
        if (t.Length == 0) return string.Empty;

        var low = t.ToLowerInvariant();
        if (low.StartsWith("http://", StringComparison.Ordinal) ||
            low.StartsWith("https://", StringComparison.Ordinal))
            return t;
        if (low.Contains("://", StringComparison.Ordinal))
            return t;

        // A URL-ish string has no whitespace and contains a dot (host/path).
        if (t.Any(char.IsWhiteSpace)) return string.Empty;
        if (!t.Contains('.')) return string.Empty;
        return "https://" + t;
    }

    /// <summary>Extract the registrable-ish host from a URL (best effort: Uri host, www stripped).</summary>
    public static string ExtractDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                var host = uri.Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                    host = host[4..];
                return host;
            }
        }
        catch { }
        return string.Empty;
    }

    /// <summary>
    /// Strip the browser suffix from a window title ("YouTube - Google Chrome" → "YouTube").
    /// Used both for display names and for matching a window title against history titles.
    /// </summary>
    public static string StripBrowserSuffix(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Browser";
        foreach (var marker in new[]
                 {
                     " - Google Chrome", " - Mozilla Firefox", " - Microsoft Edge",
                     " - Brave", " - Opera", " - Vivaldi", " - Chromium",
                 })
        {
            var idx = title.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return title[..idx].Trim();
        }
        return title.Trim();
    }

    /// <summary>Heuristic incognito detection from window title text.</summary>
    public static bool TitleSuggestsIncognito(string title) =>
        title.Contains("incognito", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("inprivate", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("private browsing", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stable 31-bit int from a string (used for window_id on app_items).</summary>
    public static int StableInt32(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return System.BitConverter.ToInt32(hash, 0) & 0x7FFFFFFF;
    }
}
