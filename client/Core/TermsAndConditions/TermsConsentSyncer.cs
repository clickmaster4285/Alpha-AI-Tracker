using System.Text;
using System.Text.Json;
using client.Configuration;
using Microsoft.Extensions.Logging;

namespace client.Core.TermsAndConditions;

/// <summary>
/// Syncs T&amp;C consent events to the server. Runs as part of the existing SyncService loop
/// (not a separate BackgroundService) — consent events are batched into the regular sync pass.
/// </summary>
public sealed class TermsConsentSyncer
{
    private readonly FeatureTermsRegistry _registry;
    private readonly ILogger<FeatureTermsRegistry> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public TermsConsentSyncer(FeatureTermsRegistry registry, ILogger<FeatureTermsRegistry> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Build the sync payload for all registered features. Returns null if no consent
    /// events need syncing. The payload follows the existing sync conventions:
    /// { employeeId, token, entries: [...] }.
    /// </summary>
    public async Task<object?> BuildSyncPayloadAsync(string employeeId, CancellationToken ct)
    {
        var entries = new List<TermsConsentEntry>();

        foreach (var feature in _registry.Features)
        {
            var record = await _registry.GetRecordAsync(feature.FeatureId, ct);
            if (record == null) continue;

            entries.Add(new TermsConsentEntry
            {
                FeatureId = feature.FeatureId,
                TermsVersion = record.Version,
                Action = record.RevokedAt.HasValue ? "revoked" : "accepted",
                AcceptedAt = record.AcceptedAt,
                RevokedAt = record.RevokedAt
            });
        }

        if (entries.Count == 0) return null;

        return new
        {
            employeeId,
            entries
        };
    }

    /// <summary>Serialize a consent entry to JSON bytes (for gzip compression).</summary>
    public static byte[] SerializeEntry(TermsConsentEntry entry)
    {
        return JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
    }
}

/// <summary>Single consent entry in the sync payload.</summary>
public sealed class TermsConsentEntry
{
    public string FeatureId { get; set; } = string.Empty;
    public string TermsVersion { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // "accepted" or "revoked"
    public DateTime AcceptedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
