namespace client.Configuration;

public static class EnvLoader
{
    private static readonly string[] SearchPaths =
    [
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env"),
        Path.Combine(Environment.CurrentDirectory, ".env"),
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
    ];

    public static void Load(string? customPath = null)
    {
        var path = customPath;

        if (path == null)
        {
            path = SearchPaths.FirstOrDefault(File.Exists);
        }
        else if (!File.Exists(path))
        {
            return;
        }

        if (path == null) return;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0) continue;

            var key = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            if (!string.IsNullOrWhiteSpace(key))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
