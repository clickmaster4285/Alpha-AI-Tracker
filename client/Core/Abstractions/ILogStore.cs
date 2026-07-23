using client.Core.Models;

namespace client.Core.Abstractions;

public interface ILogStore
{
    Task InitializeAsync(CancellationToken ct);
    Task StoreAsync(IReadOnlyList<ActivityLog> logs, CancellationToken ct);
    Task<IReadOnlyList<ActivityLog>> GetUnsentAsync(int limit, CancellationToken ct);
    Task MarkSentAsync(IReadOnlyList<string> ids, CancellationToken ct);
    Task<long> GetCountAsync(CancellationToken ct);
    Task CleanupAsync(TimeSpan olderThan, CancellationToken ct);
    Task SetStatusAsync(string key, string value, CancellationToken ct);
    Task<string?> GetStatusAsync(string key, CancellationToken ct);
    Task CleanupSyncedAsync(TimeSpan olderThan, CancellationToken ct);
    Task SetPermissionStatusAsync(IReadOnlyDictionary<string, bool> permissions, string sessionType, CancellationToken ct);
    Task SaveEmployeeInfoAsync(EmployeeInfo employee, CancellationToken ct);
    Task<EmployeeInfo?> GetEmployeeInfoAsync(CancellationToken ct);
    Task ClearEmployeeInfoAsync(CancellationToken ct);
    
    // Shell command storage
    Task StoreShellCommandsAsync(IReadOnlyList<ShellCommand> commands, CancellationToken ct);
    Task<IReadOnlyList<ShellCommand>> GetUnsentShellCommandsAsync(int limit, CancellationToken ct);
    Task MarkShellCommandsSentAsync(IReadOnlyList<string> ids, CancellationToken ct);
    Task CleanupShellCommandsSyncedAsync(TimeSpan olderThan, CancellationToken ct);
    Task<long> GetShellCommandCountAsync(CancellationToken ct);
}
