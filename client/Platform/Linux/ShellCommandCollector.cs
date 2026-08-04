using System.Diagnostics;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Platform.Linux;

public class ShellCommandCollector : IShellCommandCollector
{
    private readonly string _machineId;
    private readonly List<string> _missingPerms = new();

    private readonly Dictionary<string, long> _lastPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _homeDir;

    private IEnumerable<(string Name, string Path)> GetShellHistoryFiles()
    {
        return new (string Name, string Path)[]
        {
            ("bash", Path.Combine(_homeDir, ".bash_history")),
            ("zsh",  Path.Combine(_homeDir, ".zsh_history")),
            ("fish", Path.Combine(_homeDir, ".local", "share", "fish", "fish_history")),
        };
    }

    public ShellCommandCollector(string machineId)
    {
        _machineId = machineId;
        _homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                   ?? Environment.GetEnvironmentVariable("HOME")
                   ?? "/root";
    }

    public async Task<IReadOnlyList<ShellCommand>> CollectNewCommandsAsync(CancellationToken ct)
    {
        var commands = new List<ShellCommand>();
        var now = DateTime.UtcNow;
        var currentUser = Environment.UserName;

        foreach (var (shellName, histPath) in GetShellHistoryFiles())
        {
            try
            {
                var fileCommands = await ReadHistoryFileAsync(shellName, histPath, now, currentUser, ct);
                commands.AddRange(fileCommands);
            }
            catch (UnauthorizedAccessException)
            {
                if (!_missingPerms.Contains($"{shellName}_history"))
                    _missingPerms.Add($"{shellName}_history");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch { }
        }

        // Also try to read history from running shell processes via /proc
        try
        {
            var procCommands = await ReadRunningShellHistoryAsync(now, currentUser, ct);
            commands.AddRange(procCommands);
        }
        catch { }

        return commands;
    }

    public IReadOnlyDictionary<string, bool> GetAccessibleShells()
    {
        var result = new Dictionary<string, bool>();
        foreach (var (name, path) in GetShellHistoryFiles())
        {
            try
            {
                // Check if the shell is actually installed AND the history file is readable.
                // Only check shells the user uses (have the shell binary installed).
                var shellInstalled = IsShellInstalled(name);
                if (!shellInstalled)
                {
                    // Shell not installed at all — skip this check (not a missing permission)
                    result[$"{name}_history"] = true;
                    continue;
                }

                // Check if the history file exists AND is readable
                if (!File.Exists(path))
                {
                    // Shell installed but no history file yet — that's OK
                    result[$"{name}_history"] = true;
                    continue;
                }

                // Try to actually open and read a small portion to verify read access
                bool readable = false;
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var buffer = new byte[1];
                    readable = fs.Read(buffer, 0, 1) > 0;
                }
                catch (UnauthorizedAccessException)
                {
                    readable = false;
                }
                catch
                {
                    // Other IO errors — assume not accessible
                    readable = false;
                }

                result[$"{name}_history"] = readable;
            }
            catch
            {
                result[$"{name}_history"] = false;
            }
        }

        try
        {
            result["proc_shells"] = Directory.Exists("/proc");
        }
        catch { result["proc_shells"] = false; }

        return result;
    }

    private static bool IsShellInstalled(string shellName)
    {
        // Check if the shell binary exists in standard locations
        var paths = new[]
        {
            "/bin/" + shellName,
            "/usr/bin/" + shellName,
            "/usr/local/bin/" + shellName
        };
        return paths.Any(File.Exists);
    }

    public IReadOnlyList<string> MissingPermissionInstructions
    {
        get
        {
            var list = new List<string>();
            foreach (var (name, path) in GetShellHistoryFiles())
            {
                if (_missingPerms.Contains($"{name}_history"))
                {
                    list.Add(
                        $"{name} history file is not accessible: {path}\n" +
                        $"Run: sudo chmod +r \"{path}\"\n" +
                        $"Or run the application with: sudo setfacl -m u:$(whoami):r \"{path}\"");
                }
            }
            return list;
        }
    }

    private async Task<IReadOnlyList<ShellCommand>> ReadHistoryFileAsync(
        string shellName, string histPath, DateTime now, string currentUser, CancellationToken ct)
    {
        var commands = new List<ShellCommand>();

        if (!File.Exists(histPath))
            return commands;

        var fileInfo = new FileInfo(histPath);
        var currentLength = fileInfo.Length;

        var lastPos = _lastPositions.GetValueOrDefault(histPath, 0L);
        if (currentLength <= lastPos)
            return commands;

        using var fs = new FileStream(histPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        fs.Seek(lastPos, SeekOrigin.Begin);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            // zsh history format: : timestamp:duration;command
            string command;
            DateTime? cmdTime = null;

            if (shellName == "zsh" && trimmed.StartsWith(": "))
            {
                var semiIdx = trimmed.IndexOf(';');
                if (semiIdx > 0)
                {
                    var metaPart = trimmed[2..semiIdx];
                    var colonIdx = metaPart.IndexOf(':');
                    if (colonIdx > 0 && long.TryParse(metaPart[..colonIdx], out var unixTs))
                    {
                        cmdTime = DateTimeOffset.FromUnixTimeSeconds(unixTs).UtcDateTime;
                    }
                    command = trimmed[(semiIdx + 1)..].Trim();
                }
                else
                {
                    command = trimmed;
                }
            }
            // fish history format: - cmd: command
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
                Timestamp = cmdTime ?? now,
                ShellName = shellName,
                Command = command,
                WorkingDirectory = _homeDir,
                UserName = currentUser,
                Platform = "Linux",
                SessionId = SessionInfo.SessionId
            });
        }

        _lastPositions[histPath] = currentLength;
        return commands;
    }

    private async Task<IReadOnlyList<ShellCommand>> ReadRunningShellHistoryAsync(
        DateTime now, string currentUser, CancellationToken ct)
    {
        var commands = new List<ShellCommand>();
        var shellProcesses = new[] { "bash", "zsh", "fish", "sh", "dash" };

        foreach (var shell in shellProcesses)
        {
            try
            {
                var procs = Process.GetProcessesByName(shell);
                foreach (var proc in procs)
                {
                    try
                    {
                        // Read the command line of each shell process from /proc
                        var cmdlinePath = $"/proc/{proc.Id}/cmdline";
                        if (!File.Exists(cmdlinePath)) continue;

                        var content = await File.ReadAllTextAsync(cmdlinePath, ct);
                        var parts = content.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length <= 1) continue; // interactive shell, no command

                        // Skip login shells with no command (-bash, -zsh)
                        if (parts[0].StartsWith('-')) continue;

                        var cmdText = string.Join(" ", parts);
                        if (string.IsNullOrWhiteSpace(cmdText)) continue;

                        commands.Add(new ShellCommand
                        {
                            MachineId = _machineId,
                            Timestamp = now,
                            ShellName = shell,
                            Command = cmdText,
                            WorkingDirectory = GetProcCwd(proc.Id),
                            UserName = currentUser,
                            Platform = "Linux",
                            SessionId = SessionInfo.SessionId
                        });
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
        }

        return commands;
    }

    private static string? GetProcCwd(int pid)
    {
        try
        {
            var cwdPath = $"/proc/{pid}/cwd";
            if (File.Exists(cwdPath))
                return Path.GetFullPath(cwdPath);
        }
        catch { }
        return null;
    }
}
