using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AlphaAITracker.Service;

/// <summary>
/// Single-writer local store for the applied policy bundle and unsynced alerts.
/// Lives under %ProgramData%\&lt;APP_MUTEX&gt;\dlp\ (SYSTEM ACL in production).
/// </summary>
public sealed class PolicyStore : IAsyncDisposable
{
    private readonly DlpConfig _cfg;
    private readonly ILogger<PolicyStore> _logger;
    private SqliteConnection? _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initLock = new();
    private Task? _initTask;

    public PolicyStore(DlpConfig cfg, ILogger<PolicyStore> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        // Thread-safe single-flight — ??= alone races across Enforce/Policy/Alert/Pipe workers.
        lock (_initLock)
        {
            _initTask ??= InitCoreAsync();
        }
        return _initTask.WaitAsync(ct);
    }

    private async Task InitCoreAsync()
    {
        Directory.CreateDirectory(_cfg.DataDir);
        Directory.CreateDirectory(_cfg.PolicyDir);

        _conn = new SqliteConnection($"Data Source={_cfg.DbPath}");
        await _conn.OpenAsync();

        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS policy_store (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                version INTEGER NOT NULL DEFAULT 0,
                hash TEXT NOT NULL DEFAULT '',
                bundle_json TEXT NOT NULL DEFAULT '{}',
                applied_at TEXT
            );
            INSERT OR IGNORE INTO policy_store (id, version, hash, bundle_json) VALUES (1, 0, '', '{}');

            CREATE TABLE IF NOT EXISTS dlp_alerts_local (
                id TEXT PRIMARY KEY,
                rule_id TEXT,
                policy_version INTEGER NOT NULL DEFAULT 0,
                type TEXT NOT NULL DEFAULT '',
                severity TEXT NOT NULL DEFAULT 'medium',
                action_taken TEXT NOT NULL DEFAULT 'alerted',
                occurred_at TEXT NOT NULL,
                evidence_json TEXT NOT NULL DEFAULT '{}',
                is_synced INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_dlp_alerts_local_unsynced
                ON dlp_alerts_local(is_synced) WHERE is_synced = 0;

            CREATE TABLE IF NOT EXISTS enforcement_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                at TEXT NOT NULL,
                kind TEXT NOT NULL,
                detail TEXT NOT NULL DEFAULT ''
            );
            """;
        await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("PolicyStore ready at {Path}", _cfg.DbPath);
    }

    public async Task<(long Version, string Hash, string BundleJson)> GetAppliedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT version, hash, bundle_json FROM policy_store WHERE id = 1";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return (0, "", "{}");
            return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePolicyAsync(long version, string hash, string bundleJson, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                UPDATE policy_store SET version = $v, hash = $h, bundle_json = $b, applied_at = $at
                WHERE id = 1
                """;
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$b", bundleJson);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct);

            var path = Path.Combine(_cfg.PolicyDir, $"policy.v{version}.json");
            await File.WriteAllTextAsync(path, bundleJson, ct);
            _logger.LogInformation("Applied DLP policy version {Version}", version);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnqueueAlertAsync(
        string id, string? ruleId, long policyVersion, string type, string severity,
        string actionTaken, DateTimeOffset occurredAt, string evidenceJson, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO dlp_alerts_local
                    (id, rule_id, policy_version, type, severity, action_taken, occurred_at, evidence_json, is_synced, created_at)
                VALUES
                    ($id, $rule, $pv, $type, $sev, $act, $at, $ev, 0, $now)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$rule", (object?)ruleId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pv", policyVersion);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$sev", severity);
            cmd.Parameters.AddWithValue("$act", actionTaken);
            cmd.Parameters.AddWithValue("$at", occurredAt.UtcDateTime.ToString("o"));
            cmd.Parameters.AddWithValue("$ev", string.IsNullOrWhiteSpace(evidenceJson) ? "{}" : evidenceJson);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalAlert>> GetUnsyncedAlertsAsync(int limit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                SELECT id, rule_id, policy_version, type, severity, action_taken, occurred_at, evidence_json
                FROM dlp_alerts_local WHERE is_synced = 0
                ORDER BY occurred_at ASC LIMIT $lim
                """;
            cmd.Parameters.AddWithValue("$lim", limit);
            var list = new List<LocalAlert>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new LocalAlert(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    DateTimeOffset.Parse(reader.GetString(6)),
                    reader.GetString(7)));
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkAlertsSyncedAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            foreach (var id in idList)
            {
                await using var cmd = _conn!.CreateCommand();
                cmd.CommandText = "UPDATE dlp_alerts_local SET is_synced = 1 WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AuditAsync(string kind, string detail, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "INSERT INTO enforcement_audit (at, kind, detail) VALUES ($at, $k, $d)";
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$d", detail);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureOpen()
    {
        if (_conn == null)
            throw new InvalidOperationException("PolicyStore not initialized");
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn != null)
        {
            await _conn.DisposeAsync();
            _conn = null;
        }
        _gate.Dispose();
    }
}

public sealed record LocalAlert(
    string Id,
    string? RuleId,
    long PolicyVersion,
    string Type,
    string Severity,
    string ActionTaken,
    DateTimeOffset OccurredAt,
    string EvidenceJson);
