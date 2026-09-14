using System.Text.Json;

namespace client.Core.TermsAndConditions;

/// <summary>
/// Metadata for a single feature's Terms &amp; Conditions. Registered in
/// <see cref="FeatureTermsRegistry"/> at startup.
/// </summary>
public sealed class FeatureTermsEntry
{
    /// <summary>Unique feature identifier (e.g. "browser_journey", "live_view").</summary>
    public required string FeatureId { get; init; }

    /// <summary>Human-readable name shown in the modal title.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Plain-language description of what the feature does.</summary>
    public required string Description { get; init; }

    /// <summary>Semver string; bumped when legal text changes.</summary>
    public required string TermsVersion { get; init; }

    /// <summary>The full T&amp;C text displayed in the modal.</summary>
    public required string TermsText { get; init; }

    /// <summary>
    /// True = employee MUST accept to use the app at all. False = employee can decline;
    /// the feature is disabled.
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>True = employee can revoke acceptance later via Settings/Privacy.</summary>
    public bool CanRevoke { get; init; }

    /// <summary>Human-readable description of what happens on revoke.</summary>
    public string RevokeEffect { get; init; } = string.Empty;

    /// <summary>
    /// If the stored version is below this, re-acceptance is required. Normally equals
    /// <see cref="TermsVersion"/>, but can be higher to force re-acceptance when only
    /// the legal text changed without bumping the feature version.
    /// </summary>
    public string MinimumAcceptedVersion { get; init; } = string.Empty;
}

/// <summary>
/// Persisted consent record stored in app_status as JSON.
/// </summary>
public sealed class TermsConsentRecord
{
    public string Version { get; set; } = string.Empty;
    public DateTime AcceptedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static TermsConsentRecord? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TermsConsentRecord>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
