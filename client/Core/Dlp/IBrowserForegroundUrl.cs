namespace client.Core.Dlp;

/// <summary>
/// Last-known foreground browser tab URL/domain — fed by the a11y tracker,
/// consumed by the DLP upload detector (not visit-based alerting).
/// </summary>
public interface IBrowserForegroundUrl
{
    void Report(string? url, string? domain);
    bool TryGet(out string url, out string domain);
    /// <summary>True when a URL/domain was reported within <paramref name="maxAge"/>.</summary>
    bool TryGetFresh(TimeSpan maxAge, out string url, out string domain);
}

/// <summary>Process-wide cache of the active browser tab URL.</summary>
public sealed class BrowserForegroundUrlCache : IBrowserForegroundUrl
{
    private readonly object _lock = new();
    private string _url = "";
    private string _domain = "";
    private DateTime _atUtc = DateTime.MinValue;

    public void Report(string? url, string? domain)
    {
        lock (_lock)
        {
            _url = url?.Trim() ?? "";
            _domain = domain?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(_url) || !string.IsNullOrWhiteSpace(_domain))
                _atUtc = DateTime.UtcNow;
        }
    }

    public bool TryGet(out string url, out string domain)
        => TryGetFresh(TimeSpan.FromMinutes(5), out url, out domain);

    public bool TryGetFresh(TimeSpan maxAge, out string url, out string domain)
    {
        lock (_lock)
        {
            url = _url;
            domain = _domain;
            if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(domain))
                return false;
            if (_atUtc == DateTime.MinValue || DateTime.UtcNow - _atUtc > maxAge)
                return false;
            return true;
        }
    }
}
