using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using client.Core.DesktopEventBus;

namespace client.Services.Watchers;

public class RecentFilesWatcher : IObservableEventSource
{
    private readonly ILogger<RecentFilesWatcher> _logger;
    private FileSystemWatcher? _watcher;
    private bool _isActive;
    private string? _recentFilesPath;

    public string SourceName => "recentfiles";
    public bool IsActive => _isActive;

    public event EventHandler<RawDesktopEvent>? EventRaised;

    public RecentFilesWatcher(ILogger<RecentFilesWatcher> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_isActive) return Task.CompletedTask;

        var xdgDataDir = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _recentFilesPath = !string.IsNullOrEmpty(xdgDataDir)
            ? Path.Combine(xdgDataDir, "recently-used.xbel")
            : Path.Combine(home, ".local", "share", "recently-used.xbel");

        var dir = Path.GetDirectoryName(_recentFilesPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try
            {
                _watcher = new FileSystemWatcher(dir)
                {
                    Filter = "recently-used.xbel",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = false,
                };

                _watcher.Changed += OnRecentFilesChanged;
                _watcher.Error += (s, e) =>
                    _logger.LogWarning("RecentFilesWatcher error: {Msg}", e.GetException().Message);

                _watcher.EnableRaisingEvents = true;
                _isActive = true;
                _logger.LogInformation("RecentFilesWatcher started on {Path}", _recentFilesPath);

                ParseRecentFiles();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start RecentFilesWatcher");
            }
        }
        else
        {
            _logger.LogDebug("Recent files DB not found at {Path}", _recentFilesPath);
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        _isActive = false;
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _logger.LogInformation("RecentFilesWatcher stopped");
    }

    private DateTime _lastChangeTime = DateTime.MinValue;

    private void OnRecentFilesChanged(object sender, FileSystemEventArgs e)
    {
        if (!_isActive) return;

        if ((DateTime.UtcNow - _lastChangeTime).TotalMilliseconds < 1000)
            return;

        _lastChangeTime = DateTime.UtcNow;

        try
        {
            ParseRecentFiles();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error parsing recent files");
        }
    }

    private void ParseRecentFiles()
    {
        if (string.IsNullOrEmpty(_recentFilesPath) || !File.Exists(_recentFilesPath))
            return;

        var content = File.ReadAllText(_recentFilesPath);
        var doc = XDocument.Parse(content);

        if (doc.Root == null) return;

        var entries = doc.Root.Elements("bookmark")
            .OrderByDescending(e => (string?)e.Attribute("modified") ?? "")
            .Take(5);

        foreach (var entry in entries)
        {
            var href = (string?)entry.Attribute("href");
            if (string.IsNullOrEmpty(href)) continue;

            var modified = (string?)entry.Attribute("modified");
            var added = (string?)entry.Attribute("added");

            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;
            var localPath = uri.LocalPath;

            var raw = new RawDesktopEvent
            {
                Source = "recentfiles",
                EventType = "created",
                CurrentPath = localPath,
                RawData = href,
                Timestamp = DateTime.UtcNow,
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    modified,
                    added,
                    uri = href,
                }),
            };

            EventRaised?.Invoke(this, raw);
        }
    }
}
