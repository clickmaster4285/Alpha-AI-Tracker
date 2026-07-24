using System.Diagnostics;
using System.Text.RegularExpressions;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Platform.Windows;

public partial class ShellCommandCollector : IShellCommandCollector
{
    private readonly string _machineId;
    private readonly List<string> _missingPerms = new();

    // Track last read position per history file
    private readonly Dictionary<string, long> _lastPositions = new(StringComparer.OrdinalIgnoreCase);

    // PowerShell history file path
    private static readonly string PowerShellHistoryFile =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "Roaming", "Microsoft", "Windows", "PowerShell",
            "PSReadLine", "ConsoleHost_history.txt");

    public ShellCommandCollector(string machineId)
    {
        _machineId = machineId;
    }

    public async Task<IReadOnlyList<ShellCommand>> CollectNewCommandsAsync(CancellationToken ct)
    {
        var commands = new List<ShellCommand>();
        var now = DateTime.UtcNow;
        var currentUser = Environment.UserName;

        // 1. PowerShell history file
        try
        {
            var psCommands = await ReadPowerShellHistoryAsync(now, currentUser, ct);
            commands.AddRange(psCommands);
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore, we collect what we can access
        }
        catch { }

        // 2. cmd.exe history via doskey /history for running cmd processes
        try
        {
            var cmdCommands = await ReadCmdHistoryAsync(now, currentUser, ct);
            commands.AddRange(cmdCommands);
        }
        catch { }

        return commands;
    }

    public IReadOnlyDictionary<string, bool> GetAccessibleShells()
    {
        var result = new Dictionary<string, bool>();
        try
        {
            result["powershell_history"] = File.Exists(PowerShellHistoryFile);
        }
        catch { result["powershell_history"] = false; }
        result["cmd_doskey"] = true; // doskey always works within same session
        result["wsl"] = false; // WSL would need to read Linux history
        return result;
    }

    public IReadOnlyList<string> MissingPermissionInstructions
    {
        get
        {
            var list = new List<string>();
            if (_missingPerms.Contains("powershell_history"))
            {
                list.Add(
                    "PowerShell history file is not accessible. Run the application as Administrator, " +
                    "or grant read access to: " + PowerShellHistoryFile);
            }
            return list;
        }
    }

    private async Task<IReadOnlyList<ShellCommand>> ReadPowerShellHistoryAsync(
        DateTime now, string currentUser, CancellationToken ct)
    {
        var commands = new List<ShellCommand>();

        if (!File.Exists(PowerShellHistoryFile))
            return commands;

        // Get current file length
        var fileInfo = new FileInfo(PowerShellHistoryFile);
        var currentLength = fileInfo.Length;

        var lastPos = _lastPositions.GetValueOrDefault(PowerShellHistoryFile, 0L);
        if (currentLength <= lastPos)
            return commands;

        // Read new lines
        using var fs = new FileStream(PowerShellHistoryFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        fs.Seek(lastPos, SeekOrigin.Begin);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) // comment
                continue;

            // Estimate timestamp (PSReadLine doesn't store timestamps per line)
            // We'll use current time minus some offset for batch
            var estimatedTime = now.AddSeconds(-(commands.Count * 2));

            commands.Add(new ShellCommand
            {
                MachineId = _machineId,
                Timestamp = estimatedTime,
                ShellName = "powershell",
                Command = trimmed,
                WorkingDirectory = Environment.CurrentDirectory,
                UserName = currentUser,
                Platform = "Windows",
                SessionId = SessionInfo.SessionId
            });
        }

        _lastPositions[PowerShellHistoryFile] = currentLength;

        return commands;
    }

    private async Task<IReadOnlyList<ShellCommand>> ReadCmdHistoryAsync(
        DateTime now, string currentUser, CancellationToken ct)
    {
        var commands = new List<ShellCommand>();

        try
        {
            // For cmd.exe, we try to enumerate running cmd processes and get their history
            // via doskey /history (only works when run from within that cmd session)
            // Alternative: read %USERPROFILE%\AppData\Roaming\Microsoft\Windows\Command History\*
            var cmdHistoryDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Command History");

            if (Directory.Exists(cmdHistoryDir))
            {
                foreach (var histFile in Directory.GetFiles(cmdHistoryDir, "*.xml"))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(histFile, ct);
                        // Parse XML history entries
                        var matches = CmdHistoryRegex().Matches(content);
                        foreach (Match match in matches)
                        {
                            var cmdText = match.Groups[1].Value.Trim();
                            if (string.IsNullOrWhiteSpace(cmdText)) continue;

                            commands.Add(new ShellCommand
                            {
                                MachineId = _machineId,
                                Timestamp = now,
                                ShellName = "cmd",
                                Command = cmdText,
                                WorkingDirectory = Environment.CurrentDirectory,
                                UserName = currentUser,
                                Platform = "Windows",
                                SessionId = SessionInfo.SessionId
                            });
                        }
                    }
                    catch { }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore, we collect what we can access
        }
        catch { }

        return commands;
    }

    [GeneratedRegex(@"<CommandLine>([^<]+)</CommandLine>", RegexOptions.IgnoreCase)]
    private static partial Regex CmdHistoryRegex();
}
