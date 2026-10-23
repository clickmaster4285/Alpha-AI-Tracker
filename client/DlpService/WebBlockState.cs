namespace AlphaAITracker.Service;

/// <summary>
/// Live web-block snapshot shared by EnforceWorker, BlockPageServer, and the named pipe.
/// </summary>
public sealed class WebBlockState
{
    private readonly object _gate = new();
    private IReadOnlyList<string> _domains = Array.Empty<string>();
    private long _policyVersion;
    private DateTimeOffset _updatedAt = DateTimeOffset.MinValue;

    public void Set(long policyVersion, IReadOnlyList<string> domains)
    {
        lock (_gate)
        {
            _policyVersion = policyVersion;
            _domains = domains.Count == 0
                ? Array.Empty<string>()
                : domains.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public (long PolicyVersion, IReadOnlyList<string> Domains, DateTimeOffset UpdatedAt, string BlockPageUrl) Snapshot(string blockPageBaseUrl)
    {
        lock (_gate)
        {
            return (_policyVersion, _domains, _updatedAt, blockPageBaseUrl.TrimEnd('/') + "/");
        }
    }

    public IReadOnlyList<string> Domains
    {
        get { lock (_gate) return _domains; }
    }
}
