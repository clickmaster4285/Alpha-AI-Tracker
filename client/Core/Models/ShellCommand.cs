namespace client.Core.Models;

public class ShellCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MachineId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string ShellName { get; set; } = string.Empty;   // e.g., "bash", "zsh", "powershell", "cmd"
    public string? ShellPid { get; set; }
    public string Command { get; set; } = string.Empty;     // The actual command entered
    public string? WorkingDirectory { get; set; }            // Directory where command was run
    public string? ExitCode { get; set; }                    // Exit code if available
    public string UserName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
}
