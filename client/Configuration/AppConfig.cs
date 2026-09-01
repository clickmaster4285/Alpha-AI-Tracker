using System.Reflection;

namespace client.Configuration;

public class AppConfig
{
    public string ClientId { get; init; } = GenerateMachineId();
    public string DbPath { get; init; } = "data/alpha_tracker.db";
    public string? DbEncryptionKey { get; init; }
    public int CollectIntervalSec { get; init; } = 30;
    public string LogLevel { get; init; } = "Info";
    public string? ServerUrl { get; init; }
    public string? ApiKey { get; init; }

    // Browser journey (Option B — accessibility-based; see Core/BrowserAccessibility)
    public bool BrowserTrackingEnabled { get; init; } = true;
    public int BrowserAccessibilityPollSec { get; init; } = 3;   // how often the OS accessibility tree is polled
    public int BrowserJourneyIdleMinutes { get; init; } = 15;    // close journeys after this many idle minutes
    public bool BrowserCaptureIncognito { get; init; } = false;  // store incognito URLs (legal review required; default off)

    // Browser journey URL fallback — reads the browser's OWN profile history DB when the
    // a11y tree cannot expose the omnibox URL (Linux Chrome 136+ / snap Firefox). Works
    // while the browser runs; no restart, no flag, no extension.
    public bool BrowserHistoryEnabled { get; init; } = true;
    public int BrowserHistoryPollSec { get; init; } = 10;        // how often history DBs are re-scanned/re-read

    // File Explorer journey tracking (Desktop Event Bus — AT-SPI on Linux, Shell COM
    // on Windows, FileSystemWatcher + recent-files on every platform). Captures file
    // manager navigations + file create/rename/delete into app_items journeys.
    public bool FileJourneyEnabled { get; init; } = true;

    // USB / peripheral hotplug tracking (synced to the server since 2026-08-11;
    // never deleted client-side)
    public bool HardwareDevicesEnabled { get; init; } = true;

    // Software inventory — 100% EVENT-DRIVEN detection via InstalledSoftwareWatcher: it watches
    // the OS install locations (.desktop dirs, dpkg state, Start Menu, /Applications, package-
    // manager dirs) and triggers an instant rescan when software is installed/uninstalled through
    // terminal / software center / control panel / cmd / powershell / manual file delete. NO
    // minute-based periodic scan (user rule 2026-08-10).
    public bool InventoryWatchEnabled { get; init; } = true;

    // ─── Sync engine (dedicated SyncService background loop) ───
    // Collection NEVER blocks on the network anymore: SyncService drains unsent SQLite rows on
    // its own loop, in chunks bounded by BOTH row count and serialized payload bytes, with a
    // politeness pause between chunks and exponential backoff on failure. A 50k+ row backlog
    // drains in minutes without spiking CPU or adding latency to collection.
    public int SyncIntervalSec { get; init; } = 60;          // min wait between drain passes when idle
    public int SyncMaxRows { get; init; } = 1000;            // max rows per chunk
    public int SyncMaxBytes { get; init; } = 1_000_000;      // max serialized payload bytes per chunk (~1MB)
    public int SyncChunkDelayMs { get; init; } = 150;        // politeness pause between chunks
    public int SyncMaxDurationSec { get; init; } = 300;      // per-pass time budget — a huge backlog never monopolizes CPU
    public int SyncBackoffMaxSec { get; init; } = 300;       // exponential-backoff ceiling on failure (5 min)
    public bool SyncCompression { get; init; } = true;       // gzip request bodies (server: middleware.Decompress)
    public int SyncRetentionHours { get; init; } = 24;       // retention: synced app_items/app_sessions older than this are deleted client-side

    // ─── Time & Attendance (Phase 1, finalplan §3) ───
    // Idle thresholds (A.4): when does "idle" start, and how often to poll the OS
    // idle source. The away threshold is reserved for A.8's status computation.
    public bool TaEnabled { get; init; } = true;
    public int IdleThresholdSeconds { get; init; } = 120;   // when does "idle" start
    public int IdleAwayThresholdSeconds { get; init; } = 600; // when does "away" start (A.8)
    public int IdlePollSeconds { get; init; } = 30;         // poll cadence
    public int LockHysteresisSeconds { get; init; } = 30;   // screen_lock dedup window
    public int EventAggregationWindowSec { get; init; } = 300; // 5-min sync buckets (S1)
    public int TaMaxLocalRows { get; init; } = 50_000;       // unsynced row ceiling (S6)

    // ─── GPS & Location (Phase 3, finalplan §16) ───
    // Default OFF — requires OS location permission + employee consent.
    public bool LocationEnabled { get; init; } = false;
    public int LocationPollSec { get; init; } = 300;         // 5 min — not continuous GPS

    // ─── Self-update (GitHub Releases) ───
    // The client checks https://github.com/{UpdateRepo}/releases/latest for an
    // installer newer than the running VERSION, downloads it into the user data dir
    // and installs via the OS installer (pkexec dpkg / Inno Setup / dmg).
    // NO hardcoded repo here: ALPHA_UPDATE_REPO is read from .env, falling back to the
    // pre-existing REPO=.env key. When neither is set the updater is simply disabled.
    public string UpdateRepo { get; init; } = string.Empty;
    public bool UpdateEnabled { get; init; } = true;          // master switch for all update behaviour
    public int UpdateAutoCheckHours { get; init; } = 24;      // min hours between quiet background checks
    public bool UpdateAutoInstall { get; init; } = true;      // auto-download+install when a check finds a newer version

    public static AppConfig FromEnv()
    {
        return new AppConfig
        {
            ClientId = GetEnv("ALPHA_CLIENT_ID") ?? GenerateMachineId(),
            DbPath = GetEnv("ALPHA_DB_PATH") ?? "data/alpha_tracker.db",
            DbEncryptionKey = GetEnv("ALPHA_DB_ENCRYPTION_KEY"),
            CollectIntervalSec = int.TryParse(GetEnv("ALPHA_COLLECT_INTERVAL_SEC"), out var sec) ? sec : 30,
            LogLevel = GetEnv("ALPHA_LOG_LEVEL") ?? "Info",
            ServerUrl = GetEnv("ALPHA_SERVER_URL") ?? GetDefaultServerUrl(),
            ApiKey = GetEnv("ALPHA_API_KEY"),
            BrowserTrackingEnabled = GetEnv("ALPHA_BROWSER_TRACKING_ENABLED") is not ("0" or "false" or "False"),
            BrowserAccessibilityPollSec = int.TryParse(GetEnv("ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS"), out var poll) ? poll : 3,
            BrowserJourneyIdleMinutes = int.TryParse(GetEnv("ALPHA_BROWSER_JOURNEY_IDLE_MINUTES"), out var idle) ? idle : 15,
            BrowserCaptureIncognito = GetEnv("ALPHA_BROWSER_CAPTURE_INCOGNITO") is ("1" or "true" or "True"),
            BrowserHistoryEnabled = GetEnv("ALPHA_BROWSER_HISTORY_ENABLED") is not ("0" or "false" or "False"),
            BrowserHistoryPollSec = int.TryParse(GetEnv("ALPHA_BROWSER_HISTORY_POLL_SECONDS"), out var histPoll) ? histPoll : 10,
            FileJourneyEnabled = GetEnv("ALPHA_FILE_JOURNEY_ENABLED") is not ("0" or "false" or "False"),
            HardwareDevicesEnabled = GetEnv("ALPHA_HARDWARE_DEVICES_ENABLED") is not ("0" or "false" or "False"),
            InventoryWatchEnabled = GetEnv("ALPHA_INVENTORY_WATCH_ENABLED") is not ("0" or "false" or "False"),
            SyncIntervalSec = Math.Max(1, int.TryParse(GetEnv("ALPHA_SYNC_INTERVAL_SEC"), out var syncInt) ? syncInt : 60),
            SyncMaxRows = Math.Max(1, int.TryParse(GetEnv("ALPHA_SYNC_MAX_ROWS"), out var syncRows) ? syncRows : 1000),
            SyncMaxBytes = Math.Max(16 * 1024, int.TryParse(GetEnv("ALPHA_SYNC_MAX_BYTES"), out var syncBytes) ? syncBytes : 1_000_000),
            SyncChunkDelayMs = Math.Max(0, int.TryParse(GetEnv("ALPHA_SYNC_CHUNK_DELAY_MS"), out var syncDelay) ? syncDelay : 150),
            SyncMaxDurationSec = Math.Max(10, int.TryParse(GetEnv("ALPHA_SYNC_MAX_DURATION_SEC"), out var syncDur) ? syncDur : 300),
            SyncBackoffMaxSec = Math.Max(5, int.TryParse(GetEnv("ALPHA_SYNC_BACKOFF_MAX_SEC"), out var syncBack) ? syncBack : 300),
            SyncCompression = GetEnv("ALPHA_SYNC_COMPRESSION") is not ("0" or "false" or "False"),
            SyncRetentionHours = Math.Max(1, int.TryParse(GetEnv("ALPHA_SYNC_RETENTION_HOURS"), out var syncRet) ? syncRet : 24),
            UpdateRepo = FirstNonEmpty(GetEnv("ALPHA_UPDATE_REPO"), GetEnv("REPO")) ?? string.Empty,
            UpdateEnabled = GetEnv("ALPHA_UPDATE_ENABLED") is not ("0" or "false" or "False"),
            UpdateAutoCheckHours = Math.Max(1, int.TryParse(GetEnv("ALPHA_UPDATE_AUTO_CHECK_HOURS"), out var updHours) ? updHours : 24),
            UpdateAutoInstall = GetEnv("ALPHA_UPDATE_AUTO_INSTALL") is not ("0" or "false" or "False"),
            TaEnabled = GetEnv("ALPHA_TA_ENABLED") is not ("0" or "false" or "False"),
            IdleThresholdSeconds = Math.Max(10, int.TryParse(GetEnv("ALPHA_IDLE_THRESHOLD_SEC"), out var idleTh) ? idleTh : 120),
            IdleAwayThresholdSeconds = Math.Max(30, int.TryParse(GetEnv("ALPHA_IDLE_AWAY_THRESHOLD_SEC"), out var idleAway) ? idleAway : 600),
            IdlePollSeconds = Math.Max(5, int.TryParse(GetEnv("ALPHA_IDLE_POLL_SEC"), out var idlePoll) ? idlePoll : 30),
            LockHysteresisSeconds = Math.Max(5, int.TryParse(GetEnv("ALPHA_TA_LOCK_HYSTERESIS_SEC"), out var lockHys) ? lockHys : 30),
            EventAggregationWindowSec = Math.Max(60, int.TryParse(GetEnv("ALPHA_EVENT_AGGREGATION_WINDOW_SEC"), out var aggWin) ? aggWin : 300),
            TaMaxLocalRows = Math.Max(1000, int.TryParse(GetEnv("ALPHA_TA_MAX_LOCAL_ROWS"), out var taMax) ? taMax : 50_000),
            LocationEnabled = GetEnv("ALPHA_LOCATION_ENABLED") is ("1" or "true" or "True"),
            LocationPollSec = Math.Max(60, int.TryParse(GetEnv("ALPHA_LOCATION_POLL_SEC"), out var locPoll) ? locPoll : 300),
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    private static string? GetDefaultServerUrl()
    {
        var attrs = typeof(AppConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        return attrs.FirstOrDefault(a => a.Key == "DefaultServerUrl")?.Value;
    }

    private static string? GetEnv(string key)
    {
        var val = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(val) ? null : val.Trim();
    }

    private static string GenerateMachineId()
    {
        var id = Environment.GetEnvironmentVariable("ALPHA_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(id)) return id.Trim();

        // Write to the user-writable data dir (Installer-Parity Rule §6): the install
        // dir is root-owned and unwritable. Using the app dir caused .machine-id to
        // never persist, generating a NEW machine_id on every restart — breaking auth
        // and producing orphaned sessions.
        var dataDir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlphaAITracker")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "alpha-ai-tracker");
        try { Directory.CreateDirectory(dataDir); } catch { }
        var existing = Path.Combine(dataDir, ".machine-id");
        if (File.Exists(existing))
        {
            return File.ReadAllText(existing).Trim();
        }

        var newId = Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(existing, newId);
        }
        catch { }
        return newId;
    }
}
