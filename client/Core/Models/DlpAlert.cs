namespace client.Core.Models;

/// <summary>Local DLP alert row (synced by the --dlp agent process only).</summary>
public class DlpAlert
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? RuleId { get; set; }
    public string Trigger { get; set; } = "";
    public string Severity { get; set; } = "medium";
    public string FileOrUrl { get; set; } = "";
    public string? DetailJson { get; set; }
    public DateTime EventAt { get; set; } = DateTime.UtcNow;
    public bool IsSynced { get; set; }
    public DateTime? SyncedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Cached active rule from GET /dlp-rules/active.</summary>
public class DlpRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Action { get; set; } = "alert_only";
    public string Severity { get; set; } = "medium";
    public bool Enabled { get; set; } = true;
    public bool ApplyToAll { get; set; } = true;
}

/// <summary>IPC event published by the main tracker to the DLP agent.</summary>
public class DlpIpcEvent
{
    public string Type { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}
