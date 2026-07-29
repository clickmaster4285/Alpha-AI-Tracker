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

    private static readonly string[] WatchDirectories =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    };

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
}
