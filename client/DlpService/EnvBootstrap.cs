using Microsoft.Data.Sqlite;

namespace AlphaAITracker.Service;

/// <summary>
/// Bootstrap: load service.env / nearby .env (does not overwrite already-set vars),
/// then fill ALPHA_DEVICE_TOKEN from device.auth or tracker SQLite (all user profiles
/// when running as LocalSystem).
/// </summary>
internal static class EnvBootstrap
{
    public static void Apply()
    {
        LoadServiceEnv();
        LoadDotEnvNearBinary();
        TryFillFromDeviceAuthFile();
        TryFillDeviceTokenFromTrackerDb();
    }

    private static void LoadServiceEnv()
    {
        var path = Path.Combine(DeviceCredentialStore.DefaultDir, "service.env");
        if (!File.Exists(path)) return;
        ParseAndSet(File.ReadAllText(path));
        Console.WriteLine($"[DlpService] Loaded env from {path}");
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
        // Installed: {app}\Service\.env (optional)
        yield return Path.Combine(baseDir, ".env");
        // Lab: bin/Release/net10.0 → DlpService → client
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".env"));
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".env"));
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

    private static void TryFillFromDeviceAuthFile()
    {
        if (!DeviceCredentialStore.TryLoad(out var url, out var tok)) return;

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALPHA_SERVER_URL")) &&
            !string.IsNullOrWhiteSpace(url))
            Environment.SetEnvironmentVariable("ALPHA_SERVER_URL", url);

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALPHA_DEVICE_TOKEN")) &&
            !string.IsNullOrWhiteSpace(tok))
        {
            Environment.SetEnvironmentVariable("ALPHA_DEVICE_TOKEN", tok);
            Console.WriteLine(
                $"[DlpService] ALPHA_DEVICE_TOKEN filled from {DeviceCredentialStore.DefaultPath} (len={tok.Length}).");
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
            "[DlpService] No device_token yet (device.auth / tracker SQLite). " +
            "Workers will wait until the tracker logs in.");
    }

    private static IEnumerable<string> CandidateTrackerDbPaths()
    {
        var configured = Environment.GetEnvironmentVariable("ALPHA_DB_PATH");
        var dbNames = new List<string> { "aat_verify.db", "alpha_tracker.db" };
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var name = Path.GetFileName(configured.Trim().Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(name) && !dbNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                dbNames.Insert(0, name);
            if (Path.IsPathRooted(configured) && File.Exists(configured))
                yield return configured;
        }

        // Interactive user (dotnet run / double-click as admin).
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = ServiceIdentity.DataFolderName;
        foreach (var name in dbNames)
            yield return Path.Combine(local, folder, "data", name);

        // LocalSystem: scan every profile under C:\Users\*\AppData\Local\...
        string usersRoot;
        try
        {
            usersRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".."));
        }
        catch
        {
            usersRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users");
        }

        if (!Directory.Exists(usersRoot)) yield break;

        foreach (var profile in Directory.EnumerateDirectories(usersRoot))
        {
            var leaf = Path.GetFileName(profile);
            if (leaf is "Public" or "Default" or "Default User" or "All Users") continue;
            foreach (var name in dbNames)
                yield return Path.Combine(profile, "AppData", "Local", folder, "data", name);
        }
    }
}
