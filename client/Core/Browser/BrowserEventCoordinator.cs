using System.Text.RegularExpressions;
using client.Core.Browser.Abstractions;

namespace client.Core.Browser;

/// <summary>
/// Normalizes engine-specific <see cref="RawBrowserEvent"/>s into canonical
/// <see cref="BrowserEvent"/>s. Owns dedup (≤5s) and deterministic UUID mapping.
/// Downstream consumers (JourneyEngine) only ever see canonical events.
/// Dependency direction (aiplan.txt §29): Runtime → Coordinator → JourneyEngine.
/// </summary>
public sealed class BrowserEventCoordinator
{
    private readonly Func<Guid, DetectedBrowserRuntime?> _runtimeResolver;
    private readonly int _dedupSeconds;
    private readonly HashSet<(Guid Runtime, Guid Tab, BrowserEventAction Action, string? Url, string? Title)> _recent = new();
    private DateTime _lastDedupSweep = DateTime.UtcNow;

    public event EventHandler<BrowserEvent>? CanonicalEvent;

    public BrowserEventCoordinator(Func<Guid, DetectedBrowserRuntime?> runtimeResolver, int dedupSeconds = 5)
    {
        _runtimeResolver = runtimeResolver;
        _dedupSeconds = dedupSeconds > 0 ? dedupSeconds : 5;
    }

    public void Attach(IBrowserConnection connection)
    {
        connection.EventReceived += OnRawEvent;
    }

    /// <summary>Submit a raw event directly (used for initial-tab snapshot rebuilds).</summary>
    public void Publish(RawBrowserEvent raw) => OnRawEvent(null, raw);

    public void Detach(IBrowserConnection connection)
    {
        connection.EventReceived -= OnRawEvent;
    }

    private void OnRawEvent(object? sender, RawBrowserEvent raw)
    {
        var canonical = Normalize(raw);
        if (canonical == null) return;

        var now = DateTime.UtcNow;
        if ((now - _lastDedupSweep).TotalSeconds > _dedupSeconds)
        {
            _recent.Clear();
            _lastDedupSweep = now;
        }
        var key = (canonical.RuntimeId, canonical.TabId, canonical.Action, canonical.Url, canonical.Title);
        if (_recent.Contains(key)) return;
        _recent.Add(key);

        CanonicalEvent?.Invoke(this, canonical);
    }

    private BrowserEvent? Normalize(RawBrowserEvent raw)
    {
        if (!Guid.TryParse(raw.RuntimeId, out var runtimeId)) return null;
        var action = ParseAction(raw.Action);
        if (action == BrowserEventAction.Error) return null;

        var runtime = _runtimeResolver(runtimeId);
        var profileId = runtime?.Profiles.FirstOrDefault()?.Id
            ?? BrowserIdMapper.ForProfile(runtimeId, raw.ProfileId ?? "default");
        var windowId = Guid.TryParse(raw.WindowId, out var w)
            ? w
            : BrowserIdMapper.ForWindow(runtimeId, raw.WindowId ?? "0");

        var tabId = BrowserIdMapper.ForTab(runtimeId, raw.TabId ?? "unknown");
        var journeyId = BrowserIdMapper.ForJourney(runtimeId, raw.TabId ?? "unknown");

        return new BrowserEvent
        {
            RuntimeId = runtimeId,
            Engine = raw.Engine,
            ProfileId = profileId,
            WindowId = windowId,
            TabId = tabId,
            JourneyId = journeyId,
            Action = action,
            Url = string.IsNullOrWhiteSpace(raw.Url) ? null : raw.Url,
            Title = string.IsNullOrWhiteSpace(raw.Title) ? null : raw.Title,
            Domain = ExtractDomain(raw.Url),
            Timestamp = raw.Timestamp,
            Source = raw.Source,
            Incognito = raw.Incognito,
            Metadata =
            {
                ["binaryName"] = runtime?.BinaryName ?? raw.Engine.ToString(),
                ["displayName"] = runtime?.DisplayName ?? raw.Engine.ToString(),
                ["installedAppId"] = runtime?.InstalledAppId ?? string.Empty,
                ["profileName"] = runtime?.Profiles.FirstOrDefault()?.Name ?? "Default",
                ["contextLabel"] = runtime?.Profiles.FirstOrDefault()?.Name ?? "Default",
            },
        };
    }

    private static BrowserEventAction ParseAction(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return BrowserEventAction.Updated;
        return Enum.TryParse<BrowserEventAction>(raw, true, out var parsed) ? parsed : BrowserEventAction.Updated;
    }

    private static string? ExtractDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host;
        return Regex.Replace(host, "^www\\.", "", RegexOptions.CultureInvariant);
    }
}
