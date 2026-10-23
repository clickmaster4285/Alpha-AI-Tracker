namespace client.Core.Abstractions;

/// <summary>
/// Optional hook from browser accessibility into DLP web-block UX.
/// When a tab URL (or title) matches a published blocked domain, the sink opens the
/// localhost branded interstitial (hard block stays on hosts/firewall).
/// </summary>
public interface IWebBlockHitSink
{
    void OnBrowserUrl(string? url, string? processName);

    /// <summary>Same as <see cref="OnBrowserUrl"/> but also scans window title (error pages).</summary>
    void OnBrowserNavigation(string? url, string? windowTitle, string? processName)
        => OnBrowserUrl(url, processName);
}
