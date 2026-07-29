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

    /// <summary>FK → installed_applications.id, set when this session maps to a known GUI app</summary>
    public string? InstalledAppId { get; set; }
    /// <summary>FK → installed_packages.id, set when this session maps to a known CLI package</summary>
    public string? InstalledPackageId { get; set; }

    /// <summary>OS process ID at session start (for hierarchy linking across cycles)</summary>
    public int? ProcessId { get; set; }

    /// <summary>Parent OS process ID when nested under another session</summary>
    public int? ParentProcessId { get; set; }
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
    /// <summary>Full URL for browser_navigation items (from browser extension)</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>Extracted domain from URL for browser_navigation items</summary>
    public string Domain { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>OS process ID for process/runtime items nested under a terminal</summary>
    public int? ProcessId { get; set; }

    // ── Journey Event Fields (coexist with item_type) ──

    /// <summary>Generic object type: "Folder", "File", "Window", "Tab", "Page", etc.</summary>
    public string ObjectType { get; set; } = string.Empty;
    /// <summary>Action performed: "navigate", "open", "close", "create", "delete", "rename", etc.</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>Groups events into a journey (one per window/tab session)</summary>
    public string JourneyId { get; set; } = string.Empty;
    /// <summary>Deterministic ordering within a journey</summary>
    public int Sequence { get; set; }
    /// <summary>Previous location (for navigation reconstruction)</summary>
    public string PreviousPath { get; set; } = string.Empty;
    /// <summary>Current location</summary>
    public string CurrentPath { get; set; } = string.Empty;
    /// <summary>OS window identifier</summary>
    public int? WindowId { get; set; }
    /// <summary>Internal tab identifier within a window</summary>
    public int? TabId { get; set; }
    /// <summary>Flexible JSON blob for extensible metadata</summary>
    public string MetadataJson { get; set; } = "{}";
}
