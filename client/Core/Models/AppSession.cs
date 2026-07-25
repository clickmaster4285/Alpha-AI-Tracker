namespace client.Core.Models;

public class AppSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProcessName { get; set; } = string.Empty;
    public string AppDisplayName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;

    public string MachineId { get; set; } = string.Empty;
    public string? EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
}

/// <summary>
/// Generic child item for app_sessions. Replaces browser_contexts, file_explorer_contexts,
/// urls, and url_visits with a single self-referencing relational table.
/// </summary>
public class AppItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>FK → app_sessions.id</summary>
    public string AppSessionId { get; set; } = string.Empty;
    /// <summary>FK → app_items.id (nullable, for nesting: tab → terminal, tab → navigation, folder → subfolder)</summary>
    public string? ParentItemId { get; set; }
    /// <summary>Type discriminator: 'tab', 'browser_tab', 'browser_navigation', 'terminal', 'folder', 'file'</summary>
    public string ItemType { get; set; } = string.Empty;
    /// <summary>Display title: page title, file name, tab name, folder name, etc.</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>Content identifier: URL, file path, folder path, shell command, etc.</summary>
    public string Identifier { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}
