using System.Text.Json;
using client.Core.Browser.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace client.Core.Browser;

/// <summary>
/// Persistence for browser runtime state: runtimes, journeys, and debug-port leases.
/// Uses its own SQLite connection to the same DB file (separate from SqliteLogStore).
/// This is what makes recovery idempotent — state survives reboots.
/// </summary>
public sealed class BrowserRuntimeStateStore : IBrowserRuntimeStore
{
    private readonly ILogger _logger;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BrowserRuntimeStateStore(ILogger<BrowserRuntimeStateStore> logger)
    {
        _logger = logger;
    }

    public void Initialize(string dbPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlphaAITracker");
            if (!Path.IsPathRooted(dbPath))
                dbPath = Path.Combine(baseDir, dbPath);
        }
        else if (!Path.IsPathRooted(dbPath))
        {
            dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "alpha-ai-tracker", dbPath);
        }

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        using (var busy = _connection.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout = 5000;";
            busy.ExecuteNonQuery();
        }
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS browser_runtimes (
                runtime_id      TEXT PRIMARY KEY,
                engine          TEXT NOT NULL,
                binary_name     TEXT NOT NULL,
                binary_path     TEXT,
                display_name    TEXT NOT NULL DEFAULT '',
                installed_app_id TEXT,
                version         TEXT,
                user_data_dir   TEXT,
                state           TEXT NOT NULL DEFAULT 'Undetected',
                debug_port      INTEGER,
                last_seen_at    TEXT
            );
            CREATE TABLE IF NOT EXISTS browser_journeys (
                journey_id  TEXT PRIMARY KEY,
                tab_id      TEXT NOT NULL,
                runtime_id  TEXT NOT NULL,
                session_id  TEXT NOT NULL,
                opened_at   TEXT NOT NULL,
                closed_at   TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_browser_journeys_open ON browser_journeys(closed_at);
            CREATE TABLE IF NOT EXISTS browser_debug_ports (
                runtime_id  TEXT PRIMARY KEY,
                port        INTEGER NOT NULL,
                leased_at   TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        _logger.LogInformation("Browser runtime state store initialized at {Db}", dbPath);
    }

    public async Task UpsertRuntimeAsync(DetectedBrowserRuntime runtime, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO browser_runtimes
                    (runtime_id, engine, binary_name, binary_path, display_name, installed_app_id,
                     version, user_data_dir, state, debug_port, last_seen_at)
                VALUES
                    ($id, $engine, $binary_name, $binary_path, $display_name, $installed_app_id,
                     $version, $user_data_dir, $state, $debug_port, $last_seen_at)
                ON CONFLICT(runtime_id) DO UPDATE SET
                    binary_name = excluded.binary_name,
                    binary_path = excluded.binary_path,
                    display_name = excluded.display_name,
                    version = excluded.version,
                    user_data_dir = excluded.user_data_dir,
                    state = excluded.state,
                    debug_port = excluded.debug_port,
                    last_seen_at = excluded.last_seen_at
                """;
            cmd.Parameters.AddWithValue("$id", runtime.Id.ToString("N"));
            cmd.Parameters.AddWithValue("$engine", runtime.Engine.ToString());
            cmd.Parameters.AddWithValue("$binary_name", runtime.BinaryName);
            cmd.Parameters.AddWithValue("$binary_path", (object?)runtime.BinaryPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$display_name", runtime.DisplayName);
            cmd.Parameters.AddWithValue("$installed_app_id", (object?)runtime.InstalledAppId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$version", (object?)runtime.Version ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$user_data_dir", (object?)runtime.UserDataDir ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$state", runtime.State.ToString());
            cmd.Parameters.AddWithValue("$debug_port", (object?)runtime.DebugPort ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$last_seen_at", runtime.LastSeenAt?.ToString("O") is { } ts ? ts : DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DetectedBrowserRuntime>> LoadRuntimesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var result = new List<DetectedBrowserRuntime>();
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM browser_runtimes";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new DetectedBrowserRuntime
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Engine = Enum.TryParse<BrowserEngine>(reader.GetString(1), out var e) ? e : BrowserEngine.Unknown,
                    BinaryName = reader.GetString(2),
                    BinaryPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                    DisplayName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    InstalledAppId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Version = reader.IsDBNull(6) ? null : reader.GetString(6),
                    UserDataDir = reader.IsDBNull(7) ? null : reader.GetString(7),
                    State = Enum.TryParse<BrowserRuntimeState>(reader.GetString(8), out var s) ? s : BrowserRuntimeState.Undetected,
                    DebugPort = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                });
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteRuntimeAsync(Guid runtimeId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM browser_runtimes WHERE runtime_id = $id";
            cmd.Parameters.AddWithValue("$id", runtimeId.ToString("N"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertJourneyAsync(BrowserJourneyRecord journey, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO browser_journeys (journey_id, tab_id, runtime_id, session_id, opened_at, closed_at)
                VALUES ($journey, $tab, $runtime, $session, $opened, $closed)
                ON CONFLICT(journey_id) DO UPDATE SET
                    session_id = excluded.session_id,
                    closed_at = excluded.closed_at
                """;
            cmd.Parameters.AddWithValue("$journey", journey.JourneyId.ToString("N"));
            cmd.Parameters.AddWithValue("$tab", journey.TabId.ToString("N"));
            cmd.Parameters.AddWithValue("$runtime", journey.RuntimeId.ToString("N"));
            cmd.Parameters.AddWithValue("$session", journey.SessionId);
            cmd.Parameters.AddWithValue("$opened", journey.OpenedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$closed", (object?)journey.ClosedAt?.ToString("O") ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<BrowserJourneyRecord>> LoadOpenJourneysAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var result = new List<BrowserJourneyRecord>();
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT journey_id, tab_id, runtime_id, session_id, opened_at FROM browser_journeys WHERE closed_at IS NULL";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new BrowserJourneyRecord
                {
                    JourneyId = Guid.Parse(reader.GetString(0)),
                    TabId = Guid.Parse(reader.GetString(1)),
                    RuntimeId = Guid.Parse(reader.GetString(2)),
                    SessionId = reader.GetString(3),
                    OpenedAt = DateTime.Parse(reader.GetString(4)),
                });
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseJourneyAsync(Guid journeyId, string sessionId, DateTime closedAt, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE browser_journeys SET closed_at = $closed, session_id = $session WHERE journey_id = $journey";
            cmd.Parameters.AddWithValue("$closed", closedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$session", sessionId);
            cmd.Parameters.AddWithValue("$journey", journeyId.ToString("N"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAllOpenJourneysAsync(DateTime closedAt, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE browser_journeys SET closed_at = $closed WHERE closed_at IS NULL";
            cmd.Parameters.AddWithValue("$closed", closedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetPortLeaseAsync(Guid runtimeId, int port, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO browser_debug_ports (runtime_id, port, leased_at)
                VALUES ($id, $port, $leased)
                ON CONFLICT(runtime_id) DO UPDATE SET port = excluded.port, leased_at = excluded.leased_at
                """;
            cmd.Parameters.AddWithValue("$id", runtimeId.ToString("N"));
            cmd.Parameters.AddWithValue("$port", port);
            cmd.Parameters.AddWithValue("$leased", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int?> GetPortLeaseAsync(Guid runtimeId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT port FROM browser_debug_ports WHERE runtime_id = $id";
            cmd.Parameters.AddWithValue("$id", runtimeId.ToString("N"));
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long l ? (int)l : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearPortLeaseAsync(Guid runtimeId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM browser_debug_ports WHERE runtime_id = $id";
            cmd.Parameters.AddWithValue("$id", runtimeId.ToString("N"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
