using client.Core.Models;

namespace client.Core.Abstractions;

/// <summary>
/// Collects shell/terminal commands entered by the user on each platform.
/// </summary>
public interface IShellCommandCollector
{
    /// <summary>
    /// Returns new shell commands since the last call.
    /// </summary>
    Task<IReadOnlyList<ShellCommand>> CollectNewCommandsAsync(CancellationToken ct);

    /// <summary>
    /// Human-readable status of which shells/history files are accessible.
    /// </summary>
    IReadOnlyDictionary<string, bool> GetAccessibleShells();

    /// <summary>
    /// Returns instructions for granting access to shell history files.
    /// </summary>
    IReadOnlyList<string> MissingPermissionInstructions { get; }
}
