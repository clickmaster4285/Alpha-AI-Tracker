namespace client.Core.Dlp;

/// <summary>Structural removable-path detection (OS metadata only — no product names).</summary>
public static class DlpRemovablePath
{
    public static bool IsOnRemovableVolume(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            if (OperatingSystem.IsWindows())
                return IsWindowsRemovable(path);
            if (OperatingSystem.IsLinux())
                return IsLinuxRemovable(path);
            // macOS: /Volumes/* except the system volume root is typically external.
            if (OperatingSystem.IsMacOS())
            {
                var full = Path.GetFullPath(path).Replace('\\', '/');
                return full.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase)
                       && !full.Equals("/Volumes/", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static bool IsWindowsRemovable(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!string.Equals(drive.Name, root, StringComparison.OrdinalIgnoreCase))
                continue;
            return drive.DriveType == DriveType.Removable;
        }
        return false;
    }

    private static bool IsLinuxRemovable(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        return full.StartsWith("/media/", StringComparison.Ordinal)
               || full.StartsWith("/run/media/", StringComparison.Ordinal);
    }
}
