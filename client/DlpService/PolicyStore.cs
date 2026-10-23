using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AlphaAITracker.Service;

/// <summary>
/// Single-writer local store under %ProgramData%\&lt;APP_MUTEX&gt;\dlp\.
/// Policy + alert evidence are relational (no JSON columns).
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

        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS policy_meta (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    version INTEGER NOT NULL DEFAULT 0,
                    hash TEXT NOT NULL DEFAULT '',
                    applied_at TEXT
                );
                INSERT OR IGNORE INTO policy_meta (id, version, hash) VALUES (1, 0, '');

                CREATE TABLE IF NOT EXISTS policy_rules (
                    rule_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL DEFAULT '',
                    trigger TEXT NOT NULL DEFAULT '',
                    action TEXT NOT NULL DEFAULT 'alert',
                    severity TEXT NOT NULL DEFAULT 'medium',
                    alert INTEGER NOT NULL DEFAULT 1,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    schedule_enabled INTEGER NOT NULL DEFAULT 0,
                    schedule_start TEXT NOT NULL DEFAULT '',
                    schedule_end TEXT NOT NULL DEFAULT '',
                    schedule_timezone TEXT NOT NULL DEFAULT 'UTC'
                );

                CREATE TABLE IF NOT EXISTS policy_domains (
                    domain TEXT NOT NULL,
                    rule_id TEXT,
                    source TEXT NOT NULL DEFAULT 'rule',
                    PRIMARY KEY (domain, source, rule_id)
                );

                CREATE TABLE IF NOT EXISTS policy_paths (
                    rule_id TEXT NOT NULL,
                    path_pattern TEXT NOT NULL,
                    PRIMARY KEY (rule_id, path_pattern)
                );

                CREATE TABLE IF NOT EXISTS policy_schedule_days (
                    rule_id TEXT NOT NULL,
                    day TEXT NOT NULL,
                    PRIMARY KEY (rule_id, day)
                );

                CREATE TABLE IF NOT EXISTS policy_extensions (
                    rule_id TEXT NOT NULL,
                    extension TEXT NOT NULL,
                    PRIMARY KEY (rule_id, extension)
                );

                CREATE TABLE IF NOT EXISTS dlp_alerts_local (
                    id TEXT PRIMARY KEY,
                    rule_id TEXT,
                    policy_version INTEGER NOT NULL DEFAULT 0,
                    type TEXT NOT NULL DEFAULT '',
                    severity TEXT NOT NULL DEFAULT 'medium',
                    action_taken TEXT NOT NULL DEFAULT 'alerted',
                    occurred_at TEXT NOT NULL,
                    is_synced INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_dlp_alerts_local_unsynced
                    ON dlp_alerts_local(is_synced) WHERE is_synced = 0;

                CREATE TABLE IF NOT EXISTS dlp_alert_evidence (
                    alert_id TEXT NOT NULL,
                    key TEXT NOT NULL,
                    value TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (alert_id, key)
                );

                CREATE TABLE IF NOT EXISTS enforcement_audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    at TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    detail TEXT NOT NULL DEFAULT ''
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await MigrateLegacyAsync();
        _logger.LogInformation("PolicyStore ready at {Path}", _cfg.DbPath);
    }

    private async Task MigrateLegacyAsync()
    {
        // Pull old policy_store.bundle_json / evidence_json once if present.
        try
        {
            await using var check = _conn!.CreateCommand();
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='policy_store'";
            var legacy = await check.ExecuteScalarAsync() as string;
            if (legacy != null)
            {
                await using var read = _conn.CreateCommand();
                read.CommandText = "SELECT version, hash, bundle_json, applied_at FROM policy_store WHERE id = 1";
                await using var r = await read.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    var version = r.GetInt64(0);
                    var hash = r.GetString(1);
                    var bundle = r.IsDBNull(2) ? "{}" : r.GetString(2);
                    var applied = r.IsDBNull(3) ? null : r.GetString(3);
                    await r.DisposeAsync();
                    await using var up = _conn.CreateCommand();
                    up.CommandText = "UPDATE policy_meta SET version=$v, hash=$h, applied_at=$a WHERE id=1";
                    up.Parameters.AddWithValue("$v", version);
                    up.Parameters.AddWithValue("$h", hash);
                    up.Parameters.AddWithValue("$a", (object?)applied ?? DBNull.Value);
                    await up.ExecuteNonQueryAsync();
                    await ReplacePolicyFromBundleAsync(bundle);
                }
                await using var drop = _conn.CreateCommand();
                drop.CommandText = "DROP TABLE IF EXISTS policy_store";
                await drop.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Legacy policy_store migrate skipped");
        }

        try
        {
            await using var col = _conn!.CreateCommand();
            col.CommandText = "PRAGMA table_info(dlp_alerts_local)";
            var hasEvidenceJson = false;
            await using var cr = await col.ExecuteReaderAsync();
            while (await cr.ReadAsync())
            {
                if (string.Equals(cr.GetString(1), "evidence_json", StringComparison.OrdinalIgnoreCase))
                    hasEvidenceJson = true;
            }
            await cr.DisposeAsync();
            if (hasEvidenceJson)
            {
                await using var rows = _conn.CreateCommand();
                rows.CommandText = "SELECT id, evidence_json FROM dlp_alerts_local WHERE evidence_json IS NOT NULL AND evidence_json <> ''";
                await using var rr = await rows.ExecuteReaderAsync();
                var pending = new List<(string Id, string Json)>();
                while (await rr.ReadAsync())
                    pending.Add((rr.GetString(0), rr.IsDBNull(1) ? "{}" : rr.GetString(1)));
                await rr.DisposeAsync();
                foreach (var (id, json) in pending)
                    await ReplaceEvidenceAsync(id, FlatEvidence(json));

                // Rebuild alerts table without evidence_json
                await using var rebuild = _conn.CreateCommand();
                rebuild.CommandText = """
                    CREATE TABLE IF NOT EXISTS dlp_alerts_local_new (
                        id TEXT PRIMARY KEY,
                        rule_id TEXT,
                        policy_version INTEGER NOT NULL DEFAULT 0,
                        type TEXT NOT NULL DEFAULT '',
                        severity TEXT NOT NULL DEFAULT 'medium',
                        action_taken TEXT NOT NULL DEFAULT 'alerted',
                        occurred_at TEXT NOT NULL,
                        is_synced INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL
                    );
                    INSERT OR IGNORE INTO dlp_alerts_local_new
                        (id, rule_id, policy_version, type, severity, action_taken, occurred_at, is_synced, created_at)
                    SELECT id, rule_id, policy_version, type, severity, action_taken, occurred_at, is_synced, created_at
                    FROM dlp_alerts_local;
                    DROP TABLE dlp_alerts_local;
                    ALTER TABLE dlp_alerts_local_new RENAME TO dlp_alerts_local;
                    CREATE INDEX IF NOT EXISTS idx_dlp_alerts_local_unsynced
                        ON dlp_alerts_local(is_synced) WHERE is_synced = 0;
                    """;
                await rebuild.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Legacy evidence_json migrate skipped");
        }
    }

    public async Task<(long Version, string Hash, string BundleJson)> GetAppliedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            long version = 0;
            var hash = "";
            await using (var cmd = _conn!.CreateCommand())
            {
                cmd.CommandText = "SELECT version, hash FROM policy_meta WHERE id = 1";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    version = reader.GetInt64(0);
                    hash = reader.GetString(1);
                }
            }
            var bundle = await AssembleBundleUnlockedAsync(version, ct);
            return (version, hash, bundle);
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
                UPDATE policy_meta SET version = $v, hash = $h, applied_at = $at WHERE id = 1
                """;
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct);

            await ReplacePolicyFromBundleAsync(bundleJson);

            // Debug snapshot only — source of truth is relational tables above.
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
                    (id, rule_id, policy_version, type, severity, action_taken, occurred_at, is_synced, created_at)
                VALUES
                    ($id, $rule, $pv, $type, $sev, $act, $at, 0, $now)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$rule", (object?)ruleId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pv", policyVersion);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$sev", severity);
            cmd.Parameters.AddWithValue("$act", actionTaken);
            cmd.Parameters.AddWithValue("$at", occurredAt.UtcDateTime.ToString("o"));
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct);

            await ReplaceEvidenceAsync(id, FlatEvidence(evidenceJson));
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
                SELECT id, rule_id, policy_version, type, severity, action_taken, occurred_at
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
                    new Dictionary<string, string>()));
            }
            await reader.DisposeAsync();

            foreach (var a in list)
            {
                await using var ev = _conn.CreateCommand();
                ev.CommandText = "SELECT key, value FROM dlp_alert_evidence WHERE alert_id = $id";
                ev.Parameters.AddWithValue("$id", a.Id);
                await using var er = await ev.ExecuteReaderAsync(ct);
                while (await er.ReadAsync(ct))
                    a.Evidence[er.GetString(0)] = er.IsDBNull(1) ? "" : er.GetString(1);
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

    private async Task ReplacePolicyFromBundleAsync(string bundleJson)
    {
        await using (var clear = _conn!.CreateCommand())
        {
            clear.CommandText = """
                DELETE FROM policy_rules;
                DELETE FROM policy_domains;
                DELETE FROM policy_paths;
                DELETE FROM policy_schedule_days;
                DELETE FROM policy_extensions;
                """;
            await clear.ExecuteNonQueryAsync();
        }

        if (string.IsNullOrWhiteSpace(bundleJson))
            return;

        using var doc = JsonDocument.Parse(bundleJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in rules.EnumerateArray())
            {
                var id = rule.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;
                var match = rule.TryGetProperty("match", out var m) ? m : default;
                var sched = match.ValueKind == JsonValueKind.Object && match.TryGetProperty("schedule", out var s)
                    ? s : default;

                await using var ins = _conn.CreateCommand();
                ins.CommandText = """
                    INSERT OR REPLACE INTO policy_rules
                    (rule_id, name, trigger, action, severity, alert, enabled,
                     schedule_enabled, schedule_start, schedule_end, schedule_timezone)
                    VALUES ($id,$n,$t,$a,$sev,$al,$en,$se,$ss,$ee,$tz)
                    """;
                ins.Parameters.AddWithValue("$id", id);
                ins.Parameters.AddWithValue("$n", rule.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
                ins.Parameters.AddWithValue("$t", rule.TryGetProperty("trigger", out var tr) ? tr.GetString() ?? "" : "");
                ins.Parameters.AddWithValue("$a", rule.TryGetProperty("action", out var ac) ? ac.GetString() ?? "alert" : "alert");
                ins.Parameters.AddWithValue("$sev", rule.TryGetProperty("severity", out var sev) ? sev.GetString() ?? "medium" : "medium");
                ins.Parameters.AddWithValue("$al", !rule.TryGetProperty("alert", out var al) || al.GetBoolean() ? 1 : 0);
                ins.Parameters.AddWithValue("$en", !rule.TryGetProperty("enabled", out var en) || en.GetBoolean() ? 1 : 0);
                ins.Parameters.AddWithValue("$se", sched.ValueKind == JsonValueKind.Object && sched.TryGetProperty("enabled", out var sen) && sen.GetBoolean() ? 1 : 0);
                ins.Parameters.AddWithValue("$ss", sched.ValueKind == JsonValueKind.Object && sched.TryGetProperty("start", out var st) ? st.GetString() ?? "" : "");
                ins.Parameters.AddWithValue("$ee", sched.ValueKind == JsonValueKind.Object && sched.TryGetProperty("end", out var ed) ? ed.GetString() ?? "" : "");
                ins.Parameters.AddWithValue("$tz", sched.ValueKind == JsonValueKind.Object && sched.TryGetProperty("timezone", out var tz) ? tz.GetString() ?? "UTC" : "UTC");
                await ins.ExecuteNonQueryAsync();

                await InsertStringArrayAsync("policy_domains", "domain", id, "rule", match, "domains");
                await InsertStringArrayAsync("policy_paths", "path_pattern", id, null, match, "paths");
                await InsertStringArrayAsync("policy_extensions", "extension", id, null, match, "extensions");
                if (sched.ValueKind == JsonValueKind.Object && sched.TryGetProperty("days", out var days) && days.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in days.EnumerateArray())
                    {
                        var day = d.GetString();
                        if (string.IsNullOrWhiteSpace(day)) continue;
                        await using var dayCmd = _conn.CreateCommand();
                        dayCmd.CommandText = "INSERT OR IGNORE INTO policy_schedule_days (rule_id, day) VALUES ($r,$d)";
                        dayCmd.Parameters.AddWithValue("$r", id);
                        dayCmd.Parameters.AddWithValue("$d", day.Trim().ToLowerInvariant());
                        await dayCmd.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        if (root.TryGetProperty("shadowDeny", out var sd) &&
            sd.ValueKind == JsonValueKind.Object &&
            sd.TryGetProperty("domains", out var sdd) &&
            sdd.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in sdd.EnumerateArray())
            {
                var dom = d.GetString();
                if (string.IsNullOrWhiteSpace(dom)) continue;
                await using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO policy_domains (domain, rule_id, source) VALUES ($d,'', 'shadow')";
                cmd.Parameters.AddWithValue("$d", dom.Trim().ToLowerInvariant());
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    private async Task InsertStringArrayAsync(
        string table, string col, string ruleId, string? source, JsonElement match, string prop)
    {
        if (match.ValueKind != JsonValueKind.Object || !match.TryGetProperty(prop, out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var el in arr.EnumerateArray())
        {
            var v = el.GetString();
            if (string.IsNullOrWhiteSpace(v)) continue;
            await using var cmd = _conn!.CreateCommand();
            if (source != null)
            {
                cmd.CommandText = $"INSERT OR IGNORE INTO {table} ({col}, rule_id, source) VALUES ($v,$r,$s)";
                cmd.Parameters.AddWithValue("$s", source);
            }
            else if (table == "policy_paths")
            {
                cmd.CommandText = "INSERT OR IGNORE INTO policy_paths (rule_id, path_pattern) VALUES ($r,$v)";
            }
            else
            {
                cmd.CommandText = $"INSERT OR IGNORE INTO {table} (rule_id, {col}) VALUES ($r,$v)";
            }
            cmd.Parameters.AddWithValue("$v", v.Trim());
            cmd.Parameters.AddWithValue("$r", ruleId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<string> AssembleBundleUnlockedAsync(long version, CancellationToken ct)
    {
        var rules = new List<object>();
        await using (var cmd = _conn!.CreateCommand())
        {
            cmd.CommandText = """
                SELECT rule_id, name, trigger, action, severity, alert, enabled,
                       schedule_enabled, schedule_start, schedule_end, schedule_timezone
                FROM policy_rules
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var match = new Dictionary<string, object?>();
                var domains = await LoadStringsAsync(
                    "SELECT domain FROM policy_domains WHERE rule_id=$r AND source='rule'", id, ct);
                if (domains.Count > 0) match["domains"] = domains;
                var paths = await LoadStringsAsync(
                    "SELECT path_pattern FROM policy_paths WHERE rule_id=$r", id, ct);
                if (paths.Count > 0) match["paths"] = paths;
                var exts = await LoadStringsAsync(
                    "SELECT extension FROM policy_extensions WHERE rule_id=$r", id, ct);
                if (exts.Count > 0) match["extensions"] = exts;
                if (reader.GetInt64(7) == 1)
                {
                    var days = await LoadStringsAsync(
                        "SELECT day FROM policy_schedule_days WHERE rule_id=$r", id, ct);
                    match["schedule"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["start"] = reader.GetString(8),
                        ["end"] = reader.GetString(9),
                        ["timezone"] = reader.GetString(10),
                        ["days"] = days,
                    };
                }
                rules.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["name"] = reader.GetString(1),
                    ["trigger"] = reader.GetString(2),
                    ["action"] = reader.GetString(3),
                    ["severity"] = reader.GetString(4),
                    ["alert"] = reader.GetInt64(5) == 1,
                    ["enabled"] = reader.GetInt64(6) == 1,
                    ["match"] = match,
                    ["scope"] = new Dictionary<string, object?> { ["all"] = true },
                });
            }
        }

        var shadow = await LoadStringsAsync(
            "SELECT domain FROM policy_domains WHERE source='shadow'", null, ct);

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["policyVersion"] = version,
            ["rules"] = rules,
            ["shadowDeny"] = new Dictionary<string, object?>
            {
                ["domains"] = shadow,
                ["publishers"] = Array.Empty<string>(),
                ["appNames"] = Array.Empty<string>(),
            },
        });
    }

    private async Task<List<string>> LoadStringsAsync(string sql, string? ruleId, CancellationToken ct)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        if (ruleId != null)
            cmd.Parameters.AddWithValue("$r", ruleId);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    private async Task ReplaceEvidenceAsync(string alertId, Dictionary<string, string> evidence)
    {
        await using var del = _conn!.CreateCommand();
        del.CommandText = "DELETE FROM dlp_alert_evidence WHERE alert_id = $id";
        del.Parameters.AddWithValue("$id", alertId);
        await del.ExecuteNonQueryAsync();
        foreach (var (k, v) in evidence)
        {
            await using var ins = _conn.CreateCommand();
            ins.CommandText = "INSERT OR REPLACE INTO dlp_alert_evidence (alert_id, key, value) VALUES ($a,$k,$v)";
            ins.Parameters.AddWithValue("$a", alertId);
            ins.Parameters.AddWithValue("$k", k);
            ins.Parameters.AddWithValue("$v", v ?? "");
            await ins.ExecuteNonQueryAsync();
        }
    }

    internal static Dictionary<string, string> FlatEvidence(string? evidenceJson)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(evidenceJson))
            return dict;
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return dict;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                dict[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Number => p.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    JsonValueKind.Array => string.Join(",",
                        p.Value.EnumerateArray()
                            .Select(el => el.ValueKind == JsonValueKind.String
                                ? (el.GetString() ?? "")
                                : el.GetRawText())),
                    _ => p.Value.GetRawText(),
                };
            }
        }
        catch { /* ignore bad evidence */ }
        return dict;
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

public sealed class LocalAlert
{
    public LocalAlert(
        string id, string? ruleId, long policyVersion, string type, string severity,
        string actionTaken, DateTimeOffset occurredAt, Dictionary<string, string> evidence)
    {
        Id = id;
        RuleId = ruleId;
        PolicyVersion = policyVersion;
        Type = type;
        Severity = severity;
        ActionTaken = actionTaken;
        OccurredAt = occurredAt;
        Evidence = evidence;
    }

    public string Id { get; }
    public string? RuleId { get; }
    public long PolicyVersion { get; }
    public string Type { get; }
    public string Severity { get; }
    public string ActionTaken { get; }
    public DateTimeOffset OccurredAt { get; }
    public Dictionary<string, string> Evidence { get; }
}
