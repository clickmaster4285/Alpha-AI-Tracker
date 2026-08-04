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
    /// below cover the vast majority of meaningful user file operations.
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

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    public FileSystemEventWatcher(ILogger<FileSystemEventWatcher> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_isActive) return Task.CompletedTask;

        var validDirs = WatchDirectories
            .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
            .Distinct()
            .ToList();

        foreach (var dir in validDirs)
        {
            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                    IncludeSubdirectories = true,
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
                _logger.LogDebug("FileSystemWatcher started on {Dir}", dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start FileSystemWatcher on {Dir}", dir);
            }
        }

        _isActive = true;
        _logger.LogInformation("FileSystem watcher started on {Count} directories", _watchers.Count);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _isActive = false;
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
