using System.Text.Json;

namespace AlphaAITracker.Service;

internal sealed class PolicyBundle
{
    public long PolicyVersion { get; init; }
    public IReadOnlyList<PolicyRule> Rules { get; init; } = Array.Empty<PolicyRule>();
}

internal sealed class PolicyRule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Trigger { get; init; } = "";
    public string Action { get; init; } = "alert";
    public string Severity { get; init; } = "medium";
    public bool Alert { get; init; } = true;
}

internal static class PolicyBundleParser
{
    public static PolicyBundle Parse(string bundleJson)
    {
        if (string.IsNullOrWhiteSpace(bundleJson))
            return new PolicyBundle();

        using var doc = JsonDocument.Parse(bundleJson);
        var root = doc.RootElement;
        var version = root.TryGetProperty("policyVersion", out var pv) ? pv.GetInt64() : 0;
        var rules = new List<PolicyRule>();
        if (root.TryGetProperty("rules", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                rules.Add(new PolicyRule
                {
                    Id = el.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Enabled = !el.TryGetProperty("enabled", out var en) || en.GetBoolean(),
                    Trigger = el.TryGetProperty("trigger", out var tr) ? tr.GetString() ?? "" : "",
                    Action = el.TryGetProperty("action", out var ac) ? ac.GetString() ?? "alert" : "alert",
                    Severity = el.TryGetProperty("severity", out var sev) ? sev.GetString() ?? "medium" : "medium",
                    Alert = !el.TryGetProperty("alert", out var al) || al.GetBoolean(),
                });
            }
        }
        return new PolicyBundle { PolicyVersion = version, Rules = rules };
    }

    /// <summary>
    /// True when any enabled USB trigger asks for block / alert_block.
    /// </summary>
    public static bool WantsUsbStorageBlock(PolicyBundle bundle) =>
        bundle.Rules.Any(r =>
            r.Enabled
            && (r.Trigger is "usb_storage" or "usb_file")
            && (r.Action is "block" or "alert_block"));

    public static PolicyRule? FirstUsbBlockRule(PolicyBundle bundle) =>
        bundle.Rules.FirstOrDefault(r =>
            r.Enabled
            && (r.Trigger is "usb_storage" or "usb_file")
            && (r.Action is "block" or "alert_block"));
}
