using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Self-updater: checks GitHub Releases for a newer installer than the running
/// <see cref="AppInfo.Version"/>, downloads the platform asset into the USER data
/// directory (never the install dir — Installer-Parity path discipline) and hands
/// it to the OS installer (pkexec dpkg on Linux, silent Inno Setup on Windows,
/// `open` on macOS). Runs a quiet auto-check loop (persisted cadence in app_status)
/// and exposes observable state so the GUI can bind a "Check updates" button, an
/// update banner and a download/install/restart flow.
///
/// Registered as a singleton + hosted service (same pattern as SyncService), and
/// injected into MainViewModel + DashboardViewModel so the top bar and dashboard
/// banner always read the SAME state.
///
/// NOTE: all state and commands are declared EXPLICITLY (no [ObservableProperty] /
/// [RelayCommand] source generators) — the generated members made IDEs show
/// phantom "name does not exist" errors until the analyzer re-ran, even though the
/// CLI build was always clean.
/// </summary>
public class AppUpdateService : ObservableObject, IHostedService
{
    private const string LastCheckStatusKey = "update_last_check_at";
    private const string DismissedVersionKey = "update_dismissed_version";

    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly HttpClient _downloadHttp;
    private readonly ILogStore _store;
    private readonly ILogger<AppUpdateService> _logger;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private UpdateInfo? _pending;

    public AppUpdateService(AppConfig config, HttpClient http, ILogStore store, ILogger<AppUpdateService> logger)
    {
        _config = config;
        _http = http;
        _store = store;
        _logger = logger;

        // The shared HttpClient has a 30s global timeout — fine for the tiny GitHub
        // API call but lethal for a multi-hundred-MB installer download. Dedicated
        // client with a 15-minute ceiling for the actual download.
        _downloadHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

        CheckForUpdatesCommand = new AsyncRelayCommand(
            () => CheckForUpdatesCoreAsync(auto: false, CancellationToken.None));
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync);
        DismissUpdateCommand = new AsyncRelayCommand(DismissUpdateAsync);
        RestartApplicationCommand = new RelayCommand(RestartApplication);
        OpenReleasesPageCommand = new RelayCommand(OpenReleasesPage);
    }

    // ─── Commands (bound to the top bar + dashboard banner) ───

    public IRelayCommand CheckForUpdatesCommand { get; }
    public IRelayCommand InstallUpdateCommand { get; }
    public IRelayCommand DismissUpdateCommand { get; }
    public IRelayCommand RestartApplicationCommand { get; }
    public IRelayCommand OpenReleasesPageCommand { get; }

    // ─── Observable state (bound by the top bar + dashboard banner) ───

    private bool _isChecking;
    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetProperty(ref _isChecking, value))
                OnPropertyChanged(nameof(ButtonText));
        }
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                OnPropertyChanged(nameof(ButtonText));
                OnPropertyChanged(nameof(DownloadProgressText));
            }
        }
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (SetProperty(ref _downloadProgress, value))
                OnPropertyChanged(nameof(DownloadProgressText));
        }
    }

    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (SetProperty(ref _updateAvailable, value))
            {
                OnPropertyChanged(nameof(UpdateBadgeText));
                OnPropertyChanged(nameof(InstallButtonText));
                OnPropertyChanged(nameof(StatusVisible));
            }
        }
    }

    /// <summary>True after a Linux dpkg install — the new binary is on disk; only a restart applies it.</summary>
    private bool _restartReady;
    public bool RestartReady
    {
        get => _restartReady;
        private set
        {
            if (SetProperty(ref _restartReady, value))
                OnPropertyChanged(nameof(StatusVisible));
        }
    }

    private string _latestVersion = string.Empty;
    public string LatestVersion
    {
        get => _latestVersion;
        private set
        {
            if (SetProperty(ref _latestVersion, value))
            {
                OnPropertyChanged(nameof(UpdateBadgeText));
                OnPropertyChanged(nameof(InstallButtonText));
            }
        }
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
                OnPropertyChanged(nameof(StatusVisible));
        }
    }

    private string _releaseNotes = string.Empty;
    public string ReleaseNotes
    {
        get => _releaseNotes;
        private set => SetProperty(ref _releaseNotes, value);
    }

    private string _assetName = string.Empty;
    public string AssetName
    {
        get => _assetName;
        private set => SetProperty(ref _assetName, value);
    }

    public string VersionLabel => AppInfo.Version;
    public string ButtonText => IsChecking ? "Checking…" : IsDownloading ? "Downloading…" : "Check updates";
    public string DownloadProgressText => IsDownloading ? $"{(int)DownloadProgress}%" : string.Empty;
    public string UpdateBadgeText => string.IsNullOrEmpty(LatestVersion) ? "New version" : $"v{LatestVersion} available";
    public string InstallButtonText => $"Update to v{LatestVersion}";
    public bool StatusVisible => !string.IsNullOrEmpty(StatusText) && !UpdateAvailable && !RestartReady;

    /// <summary>True only when a repo was actually configured in .env (ALPHA_UPDATE_REPO or REPO).</summary>
    private bool RepoConfigured => !string.IsNullOrWhiteSpace(_config.UpdateRepo);

    // ─── IHostedService — the quiet auto-check loop ───

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.UpdateEnabled || !RepoConfigured) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunBackgroundLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { }
        return Task.CompletedTask;
    }

    private async Task RunBackgroundLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RunAutoCheckIfDueAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update auto-check loop stopped");
        }
    }

    /// <summary>
    /// Checks now if the configured interval has elapsed since the last check
    /// (persisted in app_status). Called on shell entry and by the periodic loop —
    /// never pops a dialog and never stacks on an in-flight check.
    /// </summary>
    public async Task RunAutoCheckIfDueAsync(CancellationToken ct)
    {
        if (!_config.UpdateEnabled || !RepoConfigured || IsChecking || IsDownloading) return;
        try
        {
            var last = await _store.GetStatusAsync(LastCheckStatusKey, ct);
            var lastAt = DateTime.TryParse(last, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.MinValue;

            if (DateTime.UtcNow - lastAt < TimeSpan.FromHours(Math.Max(1, _config.UpdateAutoCheckHours)))
                return;

            await CheckForUpdatesCoreAsync(auto: true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto update check skipped");
        }
    }

    // ─── Command bodies ───

    private async Task InstallUpdateAsync()
    {
        if (_pending is null) return;
        // Atomic install gate: manual + auto install share one download path — without
        // it, two callers could both pass the IsDownloading check and write the SAME
        // <asset>.part file concurrently (interleaved writes → corrupt installer).
        if (!await _installGate.WaitAsync(0)) return;
        try
        {
            await DownloadAndInstallAsync(_pending, CancellationToken.None);
        }
        finally
        {
            _installGate.Release();
        }
    }

    /// <summary>Dismisses the banner for THIS version (manual checks can still re-trigger it).</summary>
    private async Task DismissUpdateAsync()
    {
        if (string.IsNullOrEmpty(LatestVersion)) return;
        await _store.SetStatusAsync(DismissedVersionKey, LatestVersion, CancellationToken.None);
        _pending = null;
        UpdateAvailable = false;
        StatusText = $"v{LatestVersion} dismissed — check again any time.";
    }

    /// <summary>Relaunches the app so an installed update takes effect.</summary>
    private void RestartApplication()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            StatusText = "Cannot locate the app binary to restart it.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--restart",
                UseShellExecute = true
            });

            StatusText = "Restarting…";
            // The new instance carries --restart and retries the single-instance mutex,
            // so exiting NOW lets it take over. Shutdown (not Environment.Exit) so the
            // host stops cleanly and the mutex is released by the finally block.
            App.AllowShutdown = true;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (Avalonia.Application.Current?.ApplicationLifetime
                        is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown();
                    }
                    else
                    {
                        Environment.Exit(0);
                    }
                }
                catch { Environment.Exit(0); }
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Restart failed: {ex.Message}";
        }
    }

    /// <summary>Opens the repo releases page in the default browser.</summary>
    private void OpenReleasesPage()
    {
        if (!RepoConfigured)
        {
            StatusText = "No update repository configured — set ALPHA_UPDATE_REPO or REPO in .env.";
            return;
        }
        try
        {
            var url = $"https://github.com/{_config.UpdateRepo}/releases";
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the releases page: {ex.Message}";
        }
    }

    // ─── Core flow ───

    private async Task CheckForUpdatesCoreAsync(bool auto, CancellationToken ct)
    {
        // Atomic gate: the check can be triggered from the UI button, EnterShellAsync
        // and the 30-min background loop. Without this, two callers could pass the
        // IsChecking guard and BOTH auto-install (two pkexec dialogs on Linux).
        if (!await _checkGate.WaitAsync(0, ct)) return;
        try
        {
            await CheckForUpdatesInnerAsync(auto, ct);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task CheckForUpdatesInnerAsync(bool auto, CancellationToken ct)
    {
        if (IsChecking) return;
        IsChecking = true;
        StatusText = auto ? string.Empty : "Checking for updates…";
        try
        {
            if (!RepoConfigured)
            {
                StatusText = auto
                    ? string.Empty
                    : "No update repository configured — set ALPHA_UPDATE_REPO or REPO in .env.";
                return;
            }

            var (latest, hint) = await FetchLatestAsync(ct);
            await _store.SetStatusAsync(LastCheckStatusKey, DateTime.UtcNow.ToString("O"), ct);

            if (latest is null)
            {
                StatusText = auto ? string.Empty : (hint ?? "No release found on GitHub yet.");
                return;
            }

            if (UpdateInfo.CompareVersions(latest.Version, AppInfo.Version) <= 0)
            {
                UpdateAvailable = false;
                RestartReady = false;
                StatusText = auto ? string.Empty : $"You're up to date ({AppInfo.Version}).";
                return;
            }

            // A version the user already dismissed stays dismissed on auto-checks.
            var dismissed = await _store.GetStatusAsync(DismissedVersionKey, ct);
            if (auto && string.Equals(dismissed, latest.Version, StringComparison.Ordinal))
            {
                StatusText = string.Empty;
                return;
            }

            LatestVersion = latest.Version;
            ReleaseNotes = latest.ReleaseNotes;
            AssetName = latest.AssetName;
            _pending = latest;
            UpdateAvailable = true;
            StatusText = $"Update available: v{latest.Version}";

            _logger.LogDebug("Update available: {Version} ({Asset})", latest.Version, latest.AssetName);

            if (auto && _config.UpdateAutoInstall)
            {
                // True auto-update: download + install without any click. On Linux the
                // polkit dialog is the only human step (root-owned install dir); on
                // Windows the Inno installer force-closes the app itself.
                _ = Task.Run(() => AutoInstallQuietAsync(latest, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Update check failed: {ex.Message}";
            _logger.LogWarning(ex, "Update check failed");
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task AutoInstallQuietAsync(UpdateInfo info, CancellationToken ct)
    {
        if (!await _installGate.WaitAsync(0)) return; // a manual install is already in flight
        try
        {
            await DownloadAndInstallAsync(info, ct);
        }
        finally
        {
            _installGate.Release();
        }
    }

    /// <summary>Shared download → install pipeline used by BOTH the manual button and auto-install.</summary>
    private async Task DownloadAndInstallAsync(UpdateInfo info, CancellationToken ct)
    {
        try
        {
            await DownloadAsync(info, ct);
            var installed = await InstallAsync(Path.Combine(GetUpdatesDir(), info.AssetName), ct);
            if (installed)
            {
                _pending = null;
                UpdateAvailable = false; // banner hides; RestartReady (Linux) keeps the top-bar Restart button
            }
        }
        catch (Exception ex)
        {
            // Keep the banner + install button available for a manual retry.
            StatusText = $"Update failed: {ex.Message}";
            _logger.LogWarning(ex, "Update install failed");
        }
    }

    /// <summary>
    /// GET the latest non-prerelease GitHub release and pick the platform asset.
    /// Returns (null, hint) when there is nothing to install; the hint explains why
    /// (no releases / rate-limited / no asset for this platform) for the manual path.
    /// </summary>
    private async Task<(UpdateInfo? Info, string? Hint)> FetchLatestAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_config.UpdateRepo}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.TryParseAdd($"{AppInfo.PackageName}/{AppInfo.Version}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogDebug("GitHub releases check returned {Code}", (int)resp.StatusCode);
            return (null, resp.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "No GitHub release published yet."
                : resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "GitHub rate-limited the update check — try again later."
                    : $"Update check failed (HTTP {(int)resp.StatusCode}).");
        }

        var release = await resp.Content.ReadFromJsonAsync<GitHubRelease>(ct);
        if (release is null) return (null, "No release data returned by GitHub.");

        var version = UpdateInfo.NormalizeVersion(release.TagName);
        if (version is null) return (null, $"Latest release tag \"{release.TagName}\" carries no version.");

        var asset = ResolvePlatformAsset(release.Assets);
        if (asset is null)
        {
            _logger.LogDebug("No installer asset for this platform in release {Tag}", release.TagName);
            return (null, $"No installer for this platform in release {release.TagName}.");
        }

        return (new UpdateInfo
        {
            Version = version,
            TagName = release.TagName,
            ReleaseNotes = release.Body ?? string.Empty,
            AssetName = asset.Name,
            DownloadUrl = asset.BrowserDownloadUrl,
            PublishedAt = release.PublishedAt
        }, null);
    }

    private GitHubReleaseAsset? ResolvePlatformAsset(IReadOnlyList<GitHubReleaseAsset> assets)
    {
        if (OperatingSystem.IsWindows())
            return assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        if (OperatingSystem.IsLinux())
        {
            var arch = RuntimeArchSuffix();
            // Prefer the native-arch .deb (e.g. _amd64.deb); fall back to any .deb.
            return assets.FirstOrDefault(a =>
                       a.Name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase)
                       && a.Name.Contains(arch, StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(a => a.Name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsMacOS())
            return assets.FirstOrDefault(a => a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase));

        return null;
    }

    private static string RuntimeArchSuffix()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        return arch switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "amd64",
            _ => "amd64"
        };
    }

    private async Task DownloadAsync(UpdateInfo info, CancellationToken ct)
    {
        var dir = GetUpdatesDir();
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, info.AssetName);
        var part = dest + ".part";

        IsDownloading = true;
        DownloadProgress = 0;
        StatusText = $"Downloading {info.AssetName}…";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            using var resp = await _downloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long read = 0;
            while (true)
            {
                var n = await src.ReadAsync(buffer, ct);
                if (n == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) DownloadProgress = Math.Min(100, read * 100.0 / total);
            }
            await dst.FlushAsync(ct);
            File.Move(part, dest, overwrite: true);
            StatusText = $"Downloaded {info.AssetName}";
            DownloadProgress = 100;
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            throw;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Hands the downloaded installer to the OS. Returns true on success.</summary>
    private async Task<bool> InstallAsync(string installerPath, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            // The install dir is root-owned (/usr/share/alpha-ai-tracker), so dpkg
            // needs elevation — pkexec opens the polkit password dialog (same pattern
            // as the permission wizard). dpkg replaces the binary while we run; the
            // user restarts to apply.
            StatusText = "Installing — enter your password in the system dialog…";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pkexec",
                    Arguments = $"dpkg -i \"{installerPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    StatusText = "Could not launch the installer (is policykit-1 installed?).";
                    return false;
                }
                var exited = proc.WaitForExit(120_000);
                if (!exited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    StatusText = "Installation timed out. Try again.";
                    return false;
                }
                if (proc.ExitCode != 0)
                {
                    StatusText = "Installation failed — the polkit dialog may have been cancelled.";
                    return false;
                }
                StatusText = "Update installed. Restart the app to apply it.";
                RestartReady = true;
                return true;
            }
            catch (Exception ex)
            {
                StatusText = $"Install failed: {ex.Message}";
                return false;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            // Inno Setup: CloseApplications=force + AppMutex means the installer
            // TERMINATES this process itself. A detached cmd script waits for the
            // installer to finish, then relaunches the app with --restart.
            StatusText = "Installing — the app will close and reopen automatically…";
            try
            {
                var exe = Environment.ProcessPath ?? string.Empty;
                var script = Path.Combine(Path.GetTempPath(), $"aat_update_{Guid.NewGuid():N}.cmd");
                var content =
                    "@echo off\r\n" +
                    $"start /wait \"\" \"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART\r\n" +
                    $"start \"\" \"{exe}\" --restart\r\n" +
                    "del \"%~f0\"\r\n";
                await File.WriteAllTextAsync(script, content, ct);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{script}\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                StatusText = $"Install failed: {ex.Message}";
                return false;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            // Open the dmg in Finder; the user drags the app to /Applications (no
            // silent installer exists for the dmg build).
            StatusText = "Opened the disk image — drag the app into Applications.";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"\"{installerPath}\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                StatusText = $"Could not open the disk image: {ex.Message}";
                return false;
            }
        }

        StatusText = "Unsupported platform for auto-update.";
        return false;
    }

    // ─── Path helpers (Installer-Parity: never write to the install dir) ───

    /// <summary>
    /// User-writable download dir, mirroring the SQLite/log data-dir convention:
    /// %LocalAppData%\AlphaAITracker on Windows, ~/.local/share/alpha-ai-tracker elsewhere.
    /// </summary>
    public static string GetUpdatesDir()
    {
        var baseDir = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlphaAITracker")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "alpha-ai-tracker");
        return Path.Combine(baseDir, "updates");
    }
}
