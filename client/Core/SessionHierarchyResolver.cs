namespace client.Core;

public sealed class OpenSessionRecord
{
    public int ProcessId { get; init; }
    public string AppSessionId { get; init; } = string.Empty;
    public string RootItemId { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string ItemType { get; init; } = string.Empty;
}

public sealed class ParentLink
{
    public string ParentSessionId { get; init; } = string.Empty;
    public string ParentItemId { get; init; } = string.Empty;
    public int ParentProcessId { get; init; }
}

/// <summary>
/// Resolves parent-child relationships using the OS process tree and open session registry.
/// Supports: node → terminal → IDE, bash → gnome-terminal, standalone terminals (no parent).
/// </summary>
public sealed class SessionHierarchyResolver
{
    private readonly Dictionary<int, int> _processTree;
    private readonly Dictionary<int, OpenSessionRecord> _openByPid;

    public SessionHierarchyResolver(
        Dictionary<int, int> processTree,
        IEnumerable<OpenSessionRecord> existingOpen)
    {
        _processTree = processTree;
        _openByPid = existingOpen.ToDictionary(r => r.ProcessId);
    }

    public void Register(OpenSessionRecord record) => _openByPid[record.ProcessId] = record;

    public int? GetParentProcessId(int pid) =>
        _processTree.TryGetValue(pid, out var ppid) && ppid > 0 ? ppid : null;

    /// <summary>
    /// Walk the PPID chain to find the nearest tracked ancestor session/item.
    /// Returns null for standalone processes (e.g. gnome-terminal with no IDE parent).
    /// Handles VSCode sub-processes (renderers, utilities) as intermediate steps.
    /// </summary>
    public ParentLink? ResolveParent(int pid, string processName)
    {
        var visited = new HashSet<int>();
        var current = pid;

        while (current > 0 && visited.Add(current))
        {
            if (!_processTree.TryGetValue(current, out var ppid) || ppid <= 0)
                break;

            if (visited.Contains(ppid))
                break;

            if (_openByPid.TryGetValue(ppid, out var parentRecord))
            {
                if (ShouldLinkTo(parentRecord, processName))
                {
                    return new ParentLink
                    {
                        ParentSessionId = parentRecord.AppSessionId,
                        ParentItemId = parentRecord.RootItemId,
                        ParentProcessId = ppid,
                    };
                }
            }

            // Walk through intermediate processes to find IDE / terminal emulator
            string? parentName = null;
            try
            {
                parentName = System.Diagnostics.Process.GetProcessById(ppid).ProcessName;
            }
            catch
            {
                break;
            }

            // Continue walking up through ALL intermediate processes:
            // - Shells (bash, zsh), terminal emulators (gnome-terminal)
            // - Build tools (make, go, npm), runtimes (node, dotnet)
            // - IDE sub-processes with the same base name as the IDE (e.g., "code" renderers/utilities)
            // This is critical for VSCode integrated terminal: bash → code --utility → code main
            if (AppProcessClassifier.IsShellInterpreter(parentName) ||
                AppProcessClassifier.IsTerminalEmulator(parentName) ||
                AppProcessClassifier.IsBuildTool(parentName) ||
                AppProcessClassifier.IsRuntimePackage(parentName) ||
                AppProcessClassifier.IsIdeProcess(parentName))
            {
                current = ppid;
                continue;
            }

            break;
        }

        return null;
    }

    private static bool ShouldLinkTo(OpenSessionRecord parent, string childProcessName)
    {
        if (AppProcessClassifier.IsIdeProcess(parent.ProcessName))
            return AppProcessClassifier.IsShellInterpreter(childProcessName) ||
                   AppProcessClassifier.IsTerminalEmulator(childProcessName) ||
                   AppProcessClassifier.IsRuntimePackage(childProcessName) ||
                   AppProcessClassifier.IsBuildTool(childProcessName) ||
                   parent.ItemType == "terminal";

        if (AppProcessClassifier.IsTerminalEmulator(parent.ProcessName))
            return AppProcessClassifier.IsShellInterpreter(childProcessName) ||
                   AppProcessClassifier.IsRuntimePackage(childProcessName) ||
                   AppProcessClassifier.IsBuildTool(childProcessName);

        if (parent.ItemType == "terminal")
            return AppProcessClassifier.IsShellInterpreter(childProcessName) ||
                   AppProcessClassifier.IsRuntimePackage(childProcessName) ||
                   AppProcessClassifier.IsBuildTool(childProcessName);

        if (parent.ItemType == "tab" && AppProcessClassifier.IsShellInterpreter(childProcessName))
            return true;

        return false;
    }
}
