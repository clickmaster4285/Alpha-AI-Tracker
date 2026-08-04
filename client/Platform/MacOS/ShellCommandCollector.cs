using System.Diagnostics;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Platform.MacOS;

public class ShellCommandCollector : IShellCommandCollector
{
    private readonly string _machineId;
    private readonly List<string> _missingPerms = new();
    private readonly Dictionary<string, long> _lastPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _homeDir;

    public ShellCommandCollector(string machineId)
    {
        _machineId = machineId;
        _homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                   ?? Environment.GetEnvironmentVariable("HOME")
                   ?? "/Users/Shared";
    }

    public async Task<IReadOnlyList<ShellCommand>> CollectNewCommandsAsync(CancellationToken ct)
    {
        var commands = new List<ShellCommand>();
        var now = DateTime.UtcNow;
        var currentUser = Environment.UserName;

        // 1. Bash history
        await ReadHistoryFileAsync("bash",
            Path.Combine(_homeDir, ".bash_history"),
            now, currentUser, commands, ct);

        // 2. Bash_profile history (sometimes used instead)
        await ReadHistoryFileAsync("bash",
            Path.Combine(_homeDir, ".bash_history"),
            now, currentUser, commands, ct);

        // 3. Zsh history (.zsh_history uses same format as Linux)
        await ReadHistoryFileAsync("zsh",
            Path.Combine(_homeDir, ".zsh_history"),
            now, currentUser, commands, ct);

        // 4. Zsh history alternative location
        await ReadHistoryFileAsync("zsh",
            Path.Combine(_homeDir, ".zhistory"),
            now, currentUser, commands, ct);

        // 5. Fish history
        await ReadHistoryFileAsync("fish",
            Path.Combine(_homeDir, ".local", "share", "fish", "fish_history"),
            now, currentUser, commands, ct);

        return commands;
    }

    public IReadOnlyDictionary<string, bool> GetAccessibleShells()
    {
        var result = new Dictionary<string, bool>();
        var paths = new[]
        {
            ("bash", Path.Combine(_homeDir, ".bash_history")),
            ("zsh", Path.Combine(_homeDir, ".zsh_history")),
            ("fish", Path.Combine(_homeDir, ".local", "share", "fish", "fish_history")),
        };
        foreach (var (name, path) in paths)
        {
            try { result[$"{name}_history"] = File.Exists(path); }
            catch { result[$"{name}_history"] = false; }
        }
        return result;
    }

    public IReadOnlyList<string> MissingPermissionInstructions
    {
        get
        {
            var list = new List<string>();
            if (_missingPerms.Contains("bash_history"))
                list.Add("Grant Full Disk Access to Alpha AI Tracker in System Settings → Privacy & Security → Full Disk Access");
            if (_missingPerms.Contains("zsh_history"))
                list.Add("Grant Full Disk Access to Alpha AI Tracker in System Settings → Privacy & Security → Full Disk Access");
            return list;
        }
    }

    private async Task ReadHistoryFileAsync(
        string shellName, string histPath, DateTime now,
        string currentUser, List<ShellCommand> commands, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(histPath)) return;

            var fileInfo = new FileInfo(histPath);
            var currentLength = fileInfo.Length;
            var lastPos = _lastPositions.GetValueOrDefault(histPath, 0L);
            if (currentLength <= lastPos) return;

            using var fs = new FileStream(histPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            fs.Seek(lastPos, SeekOrigin.Begin);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // zsh format
                string command;
                if (shellName == "zsh" && trimmed.StartsWith(": "))
                {
                    var semiIdx = trimmed.IndexOf(';');
                    command = semiIdx > 0
                        ? trimmed[(semiIdx + 1)..].Trim()
                        : trimmed;
                }
                else if (shellName == "fish" && trimmed.StartsWith("- cmd: "))
                {
                    command = trimmed["- cmd: ".Length..].Trim();
                }
                else
                {
                    command = trimmed;
                }

                if (string.IsNullOrWhiteSpace(command) || command.StartsWith('#'))
                    continue;

                commands.Add(new ShellCommand
                {
                    MachineId = _machineId,
                    Timestamp = now,
                    ShellName = shellName,
                    Command = command,
                    WorkingDirectory = _homeDir,
                    UserName = currentUser,
                    Platform = "macOS",
                    SessionId = SessionInfo.SessionId
                });
            }

            _lastPositions[histPath] = currentLength;
        }
        catch (UnauthorizedAccessException)
        {
            if (!_missingPerms.Contains($"{shellName}_history"))
                _missingPerms.Add($"{shellName}_history");
        }
        catch { }
    }
}
