namespace client.Core.Dlp;

/// <summary>
/// Matches admin-configured rule patterns against event subjects.
/// Patterns are comma-separated globs/keywords (case-insensitive).
/// Empty pattern matches any subject for that trigger.
/// </summary>
public static class DlpPatternMatcher
{
    public static bool Matches(string? pattern, string? subject)
    {
        var hay = subject ?? "";
        if (string.IsNullOrWhiteSpace(pattern))
            return true;

        foreach (var raw in pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) continue;
            if (raw.Contains('*') || raw.Contains('?'))
            {
                if (GlobMatch(raw, hay))
                    return true;
            }
            else if (hay.Contains(raw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool GlobMatch(string pattern, string input)
    {
        // Simple glob: * = any run, ? = one char. Case-insensitive.
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            input, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
