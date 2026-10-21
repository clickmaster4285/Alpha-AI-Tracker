using Microsoft.Data.Sqlite;

namespace AlphaAITracker.Service;

/// <summary>
/// Dev/lab bootstrap: load nearby .env into process env (does not overwrite
/// already-set variables), and if ALPHA_DEVICE_TOKEN is still empty, read it
/// from the Windows tracker SQLite (aat_verify.db / alpha_tracker.db / ALPHA_DB_PATH).
/// </summary>
internal static class EnvBootstrap
{
    public static void Apply()
    {
        LoadDotEnvNearBinary();
        TryFillDeviceTokenFromTrackerDb();
    }

    private static void LoadDotEnvNearBinary()
    {
        foreach (var path in CandidateEnvPaths())
        {
            if (!File.Exists(path)) continue;
            ParseAndSet(File.ReadAllText(path));
            Console.WriteLine($"[DlpService] Loaded env from {path}");
            return;
        }
    }

    private static IEnumerable<string> CandidateEnvPaths()
    {
        var baseDir = AppContext.BaseDirectory;
        // bin/Release/net10.0 → DlpService → client
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".env"));
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".env"));
        yield return Path.Combine(baseDir, ".env");
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.Combine(cwd, ".env");
        yield return Path.GetFullPath(Path.Combine(cwd, "..", ".env"));
    }

    private static void ParseAndSet(string content)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (val.Length >= 2 && ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                val = val[1..^1];
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, val);
        }
    }

    private static void TryFillDeviceTokenFromTrackerDb()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALPHA_DEVICE_TOKEN")))
            return;

        foreach (var dbPath in CandidateTrackerDbPaths())
        {
            if (!File.Exists(dbPath)) continue;
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT device_token FROM employee_info WHERE device_token IS NOT NULL AND length(device_token) > 0 LIMIT 1";
                var tok = cmd.ExecuteScalar() as string;
                if (string.IsNullOrWhiteSpace(tok)) continue;

                Environment.SetEnvironmentVariable("ALPHA_DEVICE_TOKEN", tok.Trim());
                Console.WriteLine(
                    $"[DlpService] ALPHA_DEVICE_TOKEN filled from {dbPath} (len={tok.Trim().Length}). " +
                    "If policy pull returns 401, re-login the tracker.");
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DlpService] Warning: could not read {dbPath}: {ex.Message}");
            }
        }

        Console.WriteLine(
            "[DlpService] No device_token found in tracker SQLite. " +
            "Login the Alpha AI Tracker client or set ALPHA_DEVICE_TOKEN in .env.");
    }

    private static IEnumerable<string> CandidateTrackerDbPaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = Path.Combine(local, "AlphaAITracker", "data");

        // Prefer ALPHA_DB_PATH from .env (e.g. data/aat_verify.db → LocalAppData\...\aat_verify.db)
        var configured = Environment.GetEnvironmentVariable("ALPHA_DB_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var name = Path.GetFileName(configured.Trim().Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(name))
                yield return Path.Combine(dataDir, name);
            if (Path.IsPathRooted(configured) && File.Exists(configured))
                yield return configured;
        }

        // Common installed / lab filenames (client/.env uses aat_verify.db today).
        yield return Path.Combine(dataDir, "aat_verify.db");
        yield return Path.Combine(dataDir, "alpha_tracker.db");
    }
}
