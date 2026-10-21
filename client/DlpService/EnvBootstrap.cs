using Microsoft.Data.Sqlite;

namespace AlphaAITracker.Service;

/// <summary>
/// Dev/lab bootstrap: load nearby .env into process env (does not overwrite
/// already-set variables), and if ALPHA_DEVICE_TOKEN is still empty, read it
/// from the Windows tracker SQLite under %LocalAppData%\AlphaAITracker.
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
            // Do not clobber process/Machine env already set by the operator.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, val);
        }
    }

    private static void TryFillDeviceTokenFromTrackerDb()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALPHA_DEVICE_TOKEN")))
            return;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(local, "AlphaAITracker", "data", "alpha_tracker.db");
        if (!File.Exists(dbPath)) return;

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT device_token FROM employee_info WHERE device_token IS NOT NULL AND length(device_token) > 0 LIMIT 1";
            var tok = cmd.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(tok))
            {
                Environment.SetEnvironmentVariable("ALPHA_DEVICE_TOKEN", tok.Trim());
                Console.WriteLine("[DlpService] ALPHA_DEVICE_TOKEN filled from tracker SQLite");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DlpService] Warning: could not read tracker device_token: {ex.Message}");
        }
    }
}
