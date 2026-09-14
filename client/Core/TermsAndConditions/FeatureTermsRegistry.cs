using client.Configuration;
using client.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace client.Core.TermsAndConditions;

/// <summary>
/// Registry of per-feature Terms &amp; Conditions entries. Each feature that collects,
/// transmits, or exposes employee data registers its T&amp;C metadata here. The framework
/// checks stored acceptance versions against registered minimums and shows modals when
/// re-acceptance is required.
/// </summary>
public sealed class FeatureTermsRegistry
{
    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly ILogger<FeatureTermsRegistry> _logger;
    private readonly List<FeatureTermsEntry> _features = new();

    public FeatureTermsRegistry(ILogStore store, AppConfig config, ILogger<FeatureTermsRegistry> logger)
    {
        _store = store;
        _config = config;
        _logger = logger;
    }

    /// <summary>All registered feature T&amp;C entries.</summary>
    public IReadOnlyList<FeatureTermsEntry> Features => _features;

    /// <summary>
    /// Register a feature's T&amp;C metadata. Called during initialization before
    /// <see cref="CheckAndPromptAsync"/>.
    /// </summary>
    public void Register(FeatureTermsEntry entry)
    {
        _features.Add(entry);
        _logger.LogDebug("T&C registered: {FeatureId} v{Version} required={Required} revocable={Revocable}",
            entry.FeatureId, entry.TermsVersion, entry.IsRequired, entry.CanRevoke);
    }

    /// <summary>
    /// Check all registered features. Returns a list of features that need employee
    /// acceptance (either never accepted or version below minimum). Required features
    /// are listed first so the modal can show them in order.
    /// </summary>
    public async Task<IReadOnlyList<FeatureTermsEntry>> GetPendingFeaturesAsync(CancellationToken ct)
    {
        var pending = new List<FeatureTermsEntry>();
        foreach (var feature in _features)
        {
            if (!IsFeatureEnabled(feature.FeatureId)) continue;

            var stored = await _store.GetStatusAsync($"terms_accepted_{feature.FeatureId}", ct);
            if (stored == null)
            {
                pending.Add(feature);
                continue;
            }

            var record = TermsConsentRecord.FromJson(stored);
            if (record == null || string.IsNullOrEmpty(record.Version))
            {
                pending.Add(feature);
                continue;
            }

            // Re-acceptance required when stored version is below the minimum
            if (string.Compare(record.Version, feature.MinimumAcceptedVersion, StringComparison.Ordinal) < 0)
            {
                pending.Add(feature);
            }
        }

        // Required features first, then optional
        return pending
            .OrderByDescending(f => f.IsRequired)
            .ThenBy(f => f.FeatureId)
            .ToList();
    }

    /// <summary>
    /// Record that the employee accepted a feature's T&amp;C at the given version.
    /// </summary>
    public async Task AcceptAsync(string featureId, string version, CancellationToken ct)
    {
        var record = new TermsConsentRecord
        {
            Version = version,
            AcceptedAt = DateTime.UtcNow,
            RevokedAt = null
        };
        await _store.SetStatusAsync($"terms_accepted_{featureId}", record.ToJson(), ct);
        _logger.LogInformation("T&C accepted: {FeatureId} v{Version}", featureId, version);
    }

    /// <summary>
    /// Record that the employee revoked acceptance of a non-required feature.
    /// </summary>
    public async Task RevokeAsync(string featureId, CancellationToken ct)
    {
        var stored = await _store.GetStatusAsync($"terms_accepted_{featureId}", ct);
        var record = stored != null ? TermsConsentRecord.FromJson(stored) : null;
        if (record == null) return;

        record.RevokedAt = DateTime.UtcNow;
        await _store.SetStatusAsync($"terms_accepted_{featureId}", record.ToJson(), ct);
        _logger.LogInformation("T&C revoked: {FeatureId}", featureId);
    }

    /// <summary>Check if a specific feature has been accepted (and not revoked).</summary>
    public async Task<bool> IsAcceptedAsync(string featureId, CancellationToken ct)
    {
        if (!IsFeatureEnabled(featureId)) return false;

        var stored = await _store.GetStatusAsync($"terms_accepted_{featureId}", ct);
        if (stored == null) return false;

        var record = TermsConsentRecord.FromJson(stored);
        if (record == null || string.IsNullOrEmpty(record.Version)) return false;

        // If revoked, not accepted
        if (record.RevokedAt.HasValue) return false;

        // Find the feature entry and check version
        var entry = _features.FirstOrDefault(f => f.FeatureId == featureId);
        if (entry == null) return false;

        return string.Compare(record.Version, entry.MinimumAcceptedVersion, StringComparison.Ordinal) >= 0;
    }

    /// <summary>Get the stored consent record for a feature (for sync to server).</summary>
    public async Task<TermsConsentRecord?> GetRecordAsync(string featureId, CancellationToken ct)
    {
        var stored = await _store.GetStatusAsync($"terms_accepted_{featureId}", ct);
        return stored != null ? TermsConsentRecord.FromJson(stored) : null;
    }

    /// <summary>Check if a feature's config-level kill switch is enabled.</summary>
    public bool IsFeatureEnabled(string featureId)
    {
        return featureId switch
        {
            "app_usage" => _config.TermsAppUsageEnabled,
            "browser_journey" => _config.TermsBrowserJourneyEnabled,
            "live_view" => _config.TermsLiveViewEnabled,
            "file_journey" => _config.TermsFileJourneyEnabled,
            _ => true // unknown features are not config-gated
        };
    }

    /// <summary>Check if the T&amp;C framework is globally enabled.</summary>
    public bool IsEnabled => _config.TermsEnabled;
}
