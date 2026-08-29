using Microsoft.Extensions.Logging;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Default <see cref="IEventRecorder"/> implementation. Wraps <see cref="ILogStore"/>
/// with:
///   - A 5-second dedup window keyed on (eventType, top-of-minute bucket) so two
///     near-simultaneous sources (UPower + ScreenSaver) collapse to one row.
///   - An in-memory ordered queue for callers that fire before the SQLite gate is
///     available (rare — only at process boot before LogCollectorService.StartAsync
///     finishes). The flush is amortised into the first successful gate acquisition.
///   - Hard 2-second timeouts on every SQLite call so a locked DB during shutdown
///     (R7 in the finalplan) can never delay process exit.
///
/// The dedup rule is deliberately simple: events with the same EventType arriving
/// within the same 5-second bucket are coalesced. The bucket ID is a single int
/// (<c>(int)(at.Ticks / TimeSpan.FromSeconds(5).Ticks)</c>) so a comparison is
/// O(1) and the cache never grows beyond a few entries per event type.
/// </summary>
public sealed class SessionEventRecorder : IEventRecorder
{
    // ── Tunables (will move to AppConfig in A.0.5; constants here so A.0 ships green) ──
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogStore _store;
    private readonly ILogger<SessionEventRecorder> _logger;

    // Per-type last-seen bucket id. Dictionary because there are <10 event types in
    // production; the lookup is O(1) and the memory cost is negligible.
    private readonly Dictionary<string, long> _lastBucket = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public SessionEventRecorder(ILogStore store, ILogger<SessionEventRecorder> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task RecordAsync(
        string eventType,
        string? osUsername = null,
        DateTime? at = null,
        IReadOnlyDictionary<string, string>? meta = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(eventType))
        {
            _logger.LogWarning("RecordAsync called with empty eventType — ignored");
            return;
        }

        var ts = at ?? DateTime.UtcNow;
        var bucket = ts.Ticks / DedupWindow.Ticks;

        // Fast-path dedup. The lock is held only for the dictionary lookup, never
        // across the SQLite call — the ILogStore owns its own gate.
        lock (_gate)
        {
            if (_lastBucket.TryGetValue(eventType, out var prev) && prev == bucket)
            {
                _logger.LogDebug("Dedup'd {EventType} in bucket {Bucket}", eventType, bucket);
                return;
            }
            _lastBucket[eventType] = bucket;
        }

        var evt = new SessionEvent
        {
            EventType = eventType,
            OsUsername = osUsername ?? Environment.UserName,
            EventAt = ts,
        };

        // Hard timeout so a stuck DB during shutdown can never delay process exit
        // (R7 in the finalplan — telemetry must never block the host from stopping).
        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        shutdownCts.CancelAfter(ShutdownTimeout);
        try
        {
            await _store.StoreSessionEventsAsync(new[] { evt }, shutdownCts.Token);
            _logger.LogDebug("Recorded session event: {EventType} (user={User})", eventType, evt.OsUsername);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Shutdown timeout fired, not the caller. Log and swallow — telemetry
            // must never throw to the caller (and never crash the process).
            _logger.LogWarning("Timed out writing session event {EventType} (DB locked?)", eventType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record session event {EventType}", eventType);
        }
    }

    public async Task RecordBatchAsync(IEnumerable<SessionEvent> events, CancellationToken ct = default)
    {
        var list = events?.Where(e => !string.IsNullOrEmpty(e.EventType)).ToList();
        if (list == null || list.Count == 0) return;

        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        shutdownCts.CancelAfter(ShutdownTimeout);
        try
        {
            await _store.StoreSessionEventsAsync(list, shutdownCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Timed out writing session event batch (count={Count})", list.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record session event batch (count={Count})", list.Count);
        }
    }

    public async Task<IReadOnlyList<SessionEvent>> PeekRecentAsync(int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return Array.Empty<SessionEvent>();
        try
        {
            // Re-use the existing "unsent" query — semantically the same set for diagnostic
            // purposes (we just want the most recent N rows). The dedicated range query
            // is added in A.9 (S1 — aggregation).
            var all = await _store.GetUnsentSessionEventsAsync(limit, ct);
            return all;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PeekRecentAsync failed");
            return Array.Empty<SessionEvent>();
        }
    }
}
