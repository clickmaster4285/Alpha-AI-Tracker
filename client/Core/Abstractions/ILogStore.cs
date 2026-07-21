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
}
