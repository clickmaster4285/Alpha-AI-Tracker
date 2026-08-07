using Microsoft.Extensions.Logging;
using client.Core.DesktopEventBus;

namespace client.Services.Watchers;

public class FileSystemEventWatcher : IObservableEventSource
{
    private readonly ILogger<FileSystemEventWatcher> _logger;
    private readonly List<FileSystemWatcher> _watchers = new();
    private bool _isActive;

    public string SourceName => "filesystem";
    public bool IsActive => _isActive;

    public event EventHandler<RawDesktopEvent>? EventRaised;

    /// <summary>
    /// Directories to watch for file system events.
    /// NOTE: SpecialFolder.UserProfile (~/) is intentionally excluded. On a typical Linux
    /// desktop it contains waydroid, flatpak, snap, Steam, .cache, .local/share/containers,
    /// and thousands of other sandbox/VM directories that generate massive event floods.
    /// Even with an exclusion list, the FileSystemWatcher still incurs kernel overhead
    /// enumerating every change in deep recursive directories. The 6 specific user folders
    /// below cover the vast majority of meaningful user file operations, and removable
    /// drive mount roots are added dynamically by <see cref="BuildMountRoots"/>.
    /// </summary>
    private static readonly string[] WatchDirectories =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    };

    /// <summary>
    /// Path fragments that, if present in an event's full path, cause the event
    /// to be silently dropped. These are VM/sandbox/container/cache directories
    /// that generate high-volume, semantically meaningless file events.
    /// </summary>
    private static readonly string[] ExcludedPathPrefixes =
    {
        ".local/share/waydroid/",
        ".var/app/",
        "snap/",
        ".cache/",
        ".local/share/Trash/",
        ".local/share/containers/",
        ".local/share/Steam/",
        "AppData/Local/Temp/",
        "Library/Caches/",
        "node_modules/",
        ".npm/",
        ".cargo/registry/",
    };

    // Tracks which excluded prefixes have been logged this session (one line per prefix total)
    private static readonly HashSet<string> _loggedExclusions = new(StringComparer.OrdinalIgnoreCase);

    // Roots a FileSystemWatcher has already been attached to (mount roots are added and
    // removed at runtime, so we diff against this set instead of the _watchers list).
    private readonly List<string> _watchedRoots = new();
    private CancellationTokenSource? _rescanCts;
    private static readonly TimeSpan MountRescanInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    public FileSystemEventWatcher(ILogger<FileSystemEventWatcher> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_isActive) return Task.CompletedTask;

        // Fixed user folders: recursive watcher per folder (their trees are the user's own
        // and were already working).
        foreach (var dir in WatchDirectories)
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                StartWatcher(dir, includeSubdirectories: true);
        }

        // Removable drive roots.
        foreach (var root in BuildMountRoots())
        {
            StartMountWatcher(root);
        }

        _isActive = true;
        _logger.LogInformation("FileSystem watcher started on {Count} directories", _watchers.Count);

        // Removable drives are mounted/unmounted at runtime (USB sticks, external disks,
        // phone mounts). Rescan the mount roots periodically so a drive plugged in AFTER
        // the app started gets a watcher without an app restart.
        _rescanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => RescanMountsLoopAsync(_rescanCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Root directories of removable/other drives:
    ///   Linux:   /media/&lt;user&gt;/* (e.g. /media/devteam/NewVolume) and /run/media/&lt;user&gt;/* (GNOME UDisks2)
    ///            and /mnt/* — each entry is one mounted volume.
    ///   Windows: drive roots of type Removable (USB).
    ///   macOS:   /Volumes/* (skips nothing — every non-system volume lands there).
    /// Copy/paste onto these drives was never tracked before because the watcher only
    /// covered the 6 fixed user folders.
    /// </summary>
    private static List<string> BuildMountRoots()
    {
        var roots = new List<string>();
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var user = Environment.UserName;
                foreach (var baseDir in new[] { $"/media/{user}", $"/run/media/{user}", "/mnt" })
                {
                    if (!Directory.Exists(baseDir)) continue;
                    foreach (var volume in Directory.EnumerateDirectories(baseDir))
                        roots.Add(volume);
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                foreach (var drive in System.IO.DriveInfo.GetDrives())
                {
                    if (drive.IsReady && drive.DriveType == System.IO.DriveType.Removable)
                        roots.Add(drive.RootDirectory.FullName);
                }
            }
            else if (OperatingSystem.IsMacOS() && Directory.Exists("/Volumes"))
            {
                foreach (var volume in Directory.EnumerateDirectories("/Volumes"))
                    roots.Add(volume);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FileSystemWatcher mount scan error: {ex.Message}");
        }
        return roots;
    }

    private void StartWatcher(string dir, bool includeSubdirectories)
    {
        if (_watchedRoots.Contains(dir, StringComparer.Ordinal)) return;
        try
        {
            var watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                IncludeSubdirectories = includeSubdirectories,
                EnableRaisingEvents = false,
            };

            watcher.Created += (s, e) => HandleFileEvent(e, "created");
            watcher.Deleted += (s, e) => HandleFileEvent(e, "deleted");
            watcher.Renamed += (s, e) => HandleRenamedEvent(e);
            watcher.Changed += (s, e) => HandleFileEvent(e, "changed");
            watcher.Error += (s, e) =>
                _logger.LogWarning("FileSystemWatcher error on {Dir}: {Msg}", dir, e.GetException().Message);

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
            _watchedRoots.Add(dir);
            _logger.LogDebug("FileSystemWatcher started on {Dir} (recursive={Recursive})", dir, includeSubdirectories);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start FileSystemWatcher on {Dir}", dir);
        }
    }

    /// <summary>
    /// Start watchers for a removable-drive mount root.
    ///
    /// A plain recursive FileSystemWatcher on the mount root breaks on many real drives:
    /// ext4 filesystem roots contain a root-owned, mode-0700 <c>lost+found</c> directory,
    /// and .NET's recursive watcher raises an Error when it cannot descend into an
    /// unreadable subdirectory — after which it stops delivering ANY events for that
    /// watch (observed live: copy/paste onto /media/devteam/NewVolume was never tracked).
    ///
    /// Instead: watch the root NON-recursively (direct children) plus one recursive
    /// watcher per readable top-level subdirectory, skipping unreadable ones. Deep,
    /// user-owned trees are still watched in full; root-owned system dirs are skipped.
    /// </summary>
    private void StartMountWatcher(string root)
    {
        if (_watchedRoots.Contains(root, StringComparer.Ordinal)) return;

        StartWatcher(root, includeSubdirectories: false);

        List<string> topLevel = new();
        try
        {
            topLevel = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cannot enumerate mount root {Root}", root);
        }

        foreach (var sub in topLevel)
        {
            if (IsDirectoryReadable(sub))
                StartWatcher(sub, includeSubdirectories: true);
            else
                _logger.LogDebug("Skipping unreadable directory under {Root}: {Sub}", root, sub);
        }
    }

    /// <summary>True if the directory can actually be read (permission-check without walking it).</summary>
    private static bool IsDirectoryReadable(string dir)
    {
        try
        {
            using var it = Directory.EnumerateDirectories(dir).GetEnumerator();
            _ = it.MoveNext(); // touching one entry forces the access check
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private async Task RescanMountsLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(MountRescanInterval, ct);

                foreach (var root in BuildMountRoots())
                {
                    if (!_watchedRoots.Contains(root, StringComparer.Ordinal))
                    {
                        _logger.LogDebug("New removable-drive mount detected: {Dir}", root);
                        StartMountWatcher(root);
                        continue;
                    }

                    // Drive was already watched but a NEW top-level folder may have been
                    // created since (the non-recursive root watcher sees it as an event but
                    // nothing attaches a recursive watcher to it yet).
                    List<string> topLevel = new();
                    try { topLevel = Directory.EnumerateDirectories(root).ToList(); }
                    catch { }

                    foreach (var sub in topLevel)
                    {
                        if (_watchedRoots.Contains(sub, StringComparer.Ordinal)) continue;
                        if (IsDirectoryReadable(sub))
                            StartWatcher(sub, includeSubdirectories: true);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mount-rescan loop ended");
        }
    }

    public void Stop()
    {
        _isActive = false;
        try { _rescanCts?.Cancel(); } catch { }
        foreach (var w in _watchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch { }
        }
        _watchers.Clear();
        _watchedRoots.Clear();
        _logger.LogInformation("FileSystem watcher stopped");
    }

    private DateTime _lastEventTime = DateTime.MinValue;
    private string? _lastEventPath;
    private string? _lastEventType;

    private void HandleFileEvent(FileSystemEventArgs e, string eventType)
    {
        if (!_isActive) return;
        if (IsExcludedPath(e.FullPath)) return;

        var now = DateTime.UtcNow;
        if (now - _lastEventTime < DebounceDelay &&
            _lastEventPath == e.FullPath &&
            _lastEventType == eventType)
            return;

        _lastEventTime = now;
        _lastEventPath = e.FullPath;
        _lastEventType = eventType;

        var raw = new RawDesktopEvent
        {
            Source = "filesystem",
            EventType = eventType,
            CurrentPath = e.FullPath,
            Timestamp = now,
        };

        EventRaised?.Invoke(this, raw);
    }

    private void HandleRenamedEvent(RenamedEventArgs e)
    {
        if (!_isActive) return;
        if (IsExcludedPath(e.FullPath))
        {
            if (IsExcludedPath(e.OldFullPath)) return;
            // The old path was meaningful but the new path is excluded — still fire
            // the event for the original path's perspective
        }

        var raw = new RawDesktopEvent
        {
            Source = "filesystem",
            EventType = "renamed",
            PreviousPath = e.OldFullPath,
            CurrentPath = e.FullPath,
            Timestamp = DateTime.UtcNow,
        };

        EventRaised?.Invoke(this, raw);
    }

    /// <summary>
    /// Returns true if the given path falls under an excluded prefix
    /// (VM/sandbox/cache directory). Logs each excluded root ONCE per
    /// session, not per event.
    /// </summary>
    private static bool IsExcludedPath(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return true;

        foreach (var prefix in ExcludedPathPrefixes)
        {
            if (fullPath.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            {
                // Log each excluded root ONCE per startup
                if (_loggedExclusions.Add(prefix))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[FileSystemWatcher] Excluding path prefix: {prefix} (hit: {fullPath})");
                }
                return true;
            }
        }

        return false;
    }
}
