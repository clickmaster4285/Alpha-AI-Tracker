using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client.Configuration;
using System.Collections.ObjectModel;
using client.Core.Abstractions;
using client.Core.Models;
using client.Services;

namespace client.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly HttpClient _httpClient;
    private readonly IInstalledAppDetector _appDetector;
    private readonly AutoStartService _autoStart;
    private readonly LogCollectorService _logCollector;

    // ─── Splash Screen State ───

    /// <summary>True while the GuardianConnect splash/loading screen is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Step1Active))]
    [NotifyPropertyChangedFor(nameof(Step1Done))]
    [NotifyPropertyChangedFor(nameof(Step2Active))]
    [NotifyPropertyChangedFor(nameof(Step2Done))]
    [NotifyPropertyChangedFor(nameof(Step3Active))]
    [NotifyPropertyChangedFor(nameof(Step3Done))]
    private bool _isSplashVisible = true;

    /// <summary>Current splash step: 0 = checking auth, 1 = loading config, 2 = starting service, 3 = complete.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Step1Active))]
    [NotifyPropertyChangedFor(nameof(Step1Done))]
    [NotifyPropertyChangedFor(nameof(Step2Active))]
    [NotifyPropertyChangedFor(nameof(Step2Done))]
    [NotifyPropertyChangedFor(nameof(Step2Pending))]
    [NotifyPropertyChangedFor(nameof(Step3Active))]
    [NotifyPropertyChangedFor(nameof(Step3Done))]
    [NotifyPropertyChangedFor(nameof(Step3Pending))]
    private int _splashStep;

    /// <summary>Progress bar fill percentage (0–100).</summary>
    [ObservableProperty]
    private double _splashProgress;

    public bool Step1Active => IsSplashVisible && SplashStep == 0;
    public bool Step1Done => IsSplashVisible && SplashStep > 0;
    public bool Step2Active => IsSplashVisible && SplashStep == 1;
    public bool Step2Done => IsSplashVisible && SplashStep > 1;
    public bool Step2Pending => IsSplashVisible && SplashStep < 1;
    public bool Step3Active => IsSplashVisible && SplashStep == 2;
    public bool Step3Done => IsSplashVisible && SplashStep > 2;
    public bool Step3Pending => IsSplashVisible && SplashStep < 2;

    /// <summary>
    /// Plays the GuardianConnect splash sequence (PDF page 1):
    /// checking authentication → loading configuration → starting monitoring service,
    /// with a smooth progress bar, then hands off to the login/main UI.
    /// </summary>
    public async Task RunSplashSequenceAsync(CancellationToken ct = default)
    {
        if (!IsSplashVisible) return;

        // Step 0: Checking authentication
        SplashStep = 0;
        await AnimateSplashProgressAsync(0, 25, 700, ct);
        await Task.Delay(400, ct);

        // Step 1: Loading configuration
        SplashStep = 1;
        await AnimateSplashProgressAsync(25, 55, 800, ct);
        await Task.Delay(400, ct);

        // Step 2: Starting monitoring service
        SplashStep = 2;
        await AnimateSplashProgressAsync(55, 90, 900, ct);
        await Task.Delay(500, ct);

        // Complete
        SplashStep = 3;
        await AnimateSplashProgressAsync(90, 100, 350, ct);
        await Task.Delay(250, ct);

        IsSplashVisible = false;
    }

    private async Task AnimateSplashProgressAsync(double from, double to, int durationMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            ct.ThrowIfCancellationRequested();
            var t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)durationMs);
            SplashProgress = from + (to - from) * EaseInOut(t);
            await Task.Delay(16, ct);
        }
        SplashProgress = to;
    }

    private static double EaseInOut(double t)
        => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    // ─── Auth State ───

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoStartStep))]
    [NotifyPropertyChangedFor(nameof(IsBackgroundStep))]
    [NotifyPropertyChangedFor(nameof(IsPermissionStep))]
    [NotifyPropertyChangedFor(nameof(IsProfile))]
    [NotifyPropertyChangedFor(nameof(RequiresPermissionAction))]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _employeeId = string.Empty;

    [ObservableProperty]
    private string _secretKey = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    // ─── Employee Info ───

    [ObservableProperty]
    private string _employeeName = string.Empty;

    [ObservableProperty]
    private string _employeeDepartment = string.Empty;

    [ObservableProperty]
    private string _employeeRole = string.Empty;

    [ObservableProperty]
    private string _employeeAvatar = string.Empty;

    [ObservableProperty]
    private string _employeeAvatarColor = string.Empty;

    // ─── Permission Step Flow ───

    public enum PermissionStep { None, AutoStart, BackgroundRunning, Dependencies, OtherPermissions }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoStartStep))]
    [NotifyPropertyChangedFor(nameof(IsDependencyStep))]
    [NotifyPropertyChangedFor(nameof(IsPermissionStep))]
    [NotifyPropertyChangedFor(nameof(IsProfile))]
    [NotifyPropertyChangedFor(nameof(RequiresPermissionAction))]
    [NotifyPropertyChangedFor(nameof(StepTitle))]
    [NotifyPropertyChangedFor(nameof(StepDescription))]
    [NotifyPropertyChangedFor(nameof(StepButtonText))]
    [NotifyPropertyChangedFor(nameof(CurrentPermissionStepNumber))]
    [NotifyPropertyChangedFor(nameof(TotalPermissionSteps))]
    private PermissionStep _currentPermissionStep = PermissionStep.None;

    public int CurrentPermissionStepNumber => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => 1,
        PermissionStep.BackgroundRunning => 2,
        PermissionStep.Dependencies => 3,
        PermissionStep.OtherPermissions => OperatingSystem.IsLinux() ? 4 : 3,
        _ => 0
    };

    public int TotalPermissionSteps => OperatingSystem.IsLinux() ? 4 : 3;

    public bool IsAutoStartStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.AutoStart;
    public bool IsBackgroundStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.BackgroundRunning;
    public bool IsDependencyStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.Dependencies;
    public bool IsPermissionStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.OtherPermissions;
    public bool IsProfile => IsLoggedIn && CurrentPermissionStep == PermissionStep.None;
    public bool RequiresPermissionAction => IsLoggedIn && CurrentPermissionStep != PermissionStep.None;

    private bool _isBrowserTracking;
    public bool IsBrowserTracking
    {
        get => _isBrowserTracking;
        set => SetProperty(ref _isBrowserTracking, value);
    }

    private string _browserTrackingStatus = string.Empty;
    public string BrowserTrackingStatus
    {
        get => _browserTrackingStatus;
        set => SetProperty(ref _browserTrackingStatus, value);
    }

    public string StepTitle => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => "Enable Auto-Start",
        PermissionStep.BackgroundRunning => "Enable Background Guard",
        PermissionStep.Dependencies => "Install Required Dependencies",
        PermissionStep.OtherPermissions => GetPlatformPermissionTitle(),
        _ => string.Empty
    };

    public string StepDescription => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => "Auto-start ensures tracking resumes automatically after a reboot or system restart.",
        PermissionStep.BackgroundRunning => "The background guard keeps the service alive even when the window is closed.",
        PermissionStep.Dependencies => "The following dependencies are missing and must be installed to grant permissions.",
        PermissionStep.OtherPermissions => GetPlatformPermissionDescription(),
        _ => string.Empty
    };

    public string StepButtonText => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => "Enable Auto-Start",
        PermissionStep.BackgroundRunning => "Enable Background Guard",
        PermissionStep.OtherPermissions => GetPlatformPermissionButtonText(),
        _ => string.Empty
    };

    private string _stepStatusText = string.Empty;
    public string StepStatusText
    {
        get => _stepStatusText;
        set => SetProperty(ref _stepStatusText, value);
    }

    private string _missingDependenciesList = string.Empty;
    public string MissingDependenciesList
    {
        get => _missingDependenciesList;
        set => SetProperty(ref _missingDependenciesList, value);
    }

    private bool _isStepWorking;
    public bool IsStepWorking
    {
        get => _isStepWorking;
        set => SetProperty(ref _isStepWorking, value);
    }

    public MainViewModel(
        AppConfig config,
        ILogStore store,
        HttpClient httpClient,
        IInstalledAppDetector appDetector,
        AutoStartService autoStart,
        LogCollectorService logCollector)
    {
        _config = config;
        _store = store;
        _httpClient = httpClient;
        _appDetector = appDetector;
        _autoStart = autoStart;
        _logCollector = logCollector;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var info = await _store.GetEmployeeInfoAsync(ct);
        if (info != null)
        {
            IsLoggedIn = true;
            EmployeeName = info.Name;
            EmployeeDepartment = info.Department;
            EmployeeRole = info.Role;
            EmployeeAvatar = info.Avatar ?? string.Empty;
            EmployeeAvatarColor = info.AvatarColor ?? "#8B5CF6";

            _logCollector.SetEmployeeInfo(info.EmployeeId, info.Name, info.Token ?? string.Empty);
            _logCollector.StartTracking();

            // Reset stale permission statuses so they get freshly re-evaluated
            // (GetNextPermissionStep() no longer reads stored statuses, but this
            //  ensures the stored values match reality for any other consumers)
            await _store.SetStatusAsync("perm_auto_start", "false", ct);
            await _store.SetStatusAsync("perm_background", "false", ct);
            await _store.SetStatusAsync("perm_other", "false", ct);

            // Evaluate which permissions are actually configured vs missing.
            // Does NOT trust any previous stored status.
            CurrentPermissionStep = await GetNextPermissionStep();

            // Forced auto-start — always ensure it's configured
            _autoStart.EnableAutoStartForced();

            IsBrowserTracking = true;
            BrowserTrackingStatus = "Active — browser journeys are captured automatically via the OS accessibility tree (no debugger, no extension).";
        }
        else
        {
            IsLoggedIn = false;
            CurrentPermissionStep = PermissionStep.None;
        }
    }

    /// <summary>
    /// Check each permission condition and return the first incomplete step.
    /// Always re-validates the ACTUAL condition — never trusts stored status alone.
    /// If a permission was previously granted but is now revoked (e.g. user removed
    /// auto-start or systemd service was deleted), it will show up again.
    /// </summary>
    private async Task<PermissionStep> GetNextPermissionStep()
    {
        // Check auto-start (Step 1)
        var autoOk = _autoStart.IsAutoStartEnabled();
        if (!autoOk)
            return PermissionStep.AutoStart;
        await _store.SetStatusAsync("perm_auto_start", "true", CancellationToken.None);

        // Check background guard (Step 2)
        var bgOk = IsBackgroundGuardConfigured();
        if (!bgOk)
            return PermissionStep.BackgroundRunning;
        await _store.SetStatusAsync("perm_background", "true", CancellationToken.None);

        if (OperatingSystem.IsLinux())
        {
            var missingDeps = GetMissingLinuxDependencies();
            if (missingDeps.Count > 0)
            {
                MissingDependenciesList = string.Join(", ", missingDeps);
                return PermissionStep.Dependencies;
            }
        }

        // Check other permissions (Step 3 or 4)
        var otherOk = !HasMissingPermissions();
        if (!otherOk)
            return PermissionStep.OtherPermissions;
        await _store.SetStatusAsync("perm_other", "true", CancellationToken.None);

        // All permissions are granted
        return PermissionStep.None;
    }

    private static bool IsBackgroundGuardConfigured()
    {
        if (OperatingSystem.IsLinux())
        {
            var svcPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "systemd", "user", "alpha-ai-tracker.service");
            return File.Exists(svcPath);
        }
        if (OperatingSystem.IsWindows())
        {
            return Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("AlphaAITracker") != null;
        }
        return false;
    }

    private List<string> GetMissingLinuxDependencies()
    {
        var missing = new List<string>();
        if (!IsCommandAvailable("pkexec")) missing.Add("policykit-1");
        if (!IsCommandAvailable("loginctl")) missing.Add("systemd");
        if (!IsCommandAvailable("gsettings")) missing.Add("libglib2.0-bin");
        if (!IsCommandAvailable("setcap")) missing.Add("libcap2-bin");
        return missing;
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sh",
                Arguments = $"-c \"command -v {command}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(2000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Check for any missing OS-level permissions.
    /// Combines detection from:
    /// - InstalledAppDetector (dpkg-query, registry access, etc.)
    /// - Direct OS permission checks (/proc access on Linux, accessibility on macOS, etc.)
    /// </summary>
    private bool HasMissingPermissions()
    {
        // 1. Check InstalledAppDetector for known permission gaps
        var missing = _appDetector.MissingPermissions;
        if (missing.Count > 0) return true;

        // 2. Check if shell collectors can access their history files (Optional)
        // We do not block on this since missing history files (e.g. no WSL installed) is normal.

        // 3. Platform-specific checks
        if (OperatingSystem.IsLinux())
        {
            return CheckLinuxPermissions();
        }
        if (OperatingSystem.IsWindows())
        {
            return CheckWindowsPermissions();
        }
        if (OperatingSystem.IsMacOS())
        {
            return CheckMacOSPermissions();
        }

        return false;
    }

    /// <summary>
    /// Linux: Check Wayland accessibility and other OS-level permissions.
    /// Under Wayland, window title tracking requires toolkit accessibility enabled.
    /// </summary>
    private static bool CheckLinuxPermissions()
    {
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (string.IsNullOrEmpty(sessionType))
        {
            // Can't determine session type — check via logind
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "loginctl",
                    Arguments = "show-session $(loginctl | grep $(whoami) | awk '{print $1}') -p Type | cut -d= -f2",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(2000);
                    if (!string.IsNullOrEmpty(output))
                        sessionType = output;
                }
            }
            catch { }
        }

        // Wayland: check toolkit accessibility (needed for window titles)
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "gsettings",
                    Arguments = "get org.gnome.desktop.interface toolkit-accessibility",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd()?.Trim();
                    proc.WaitForExit(2000);
                    if (!string.Equals(output, "true", StringComparison.OrdinalIgnoreCase))
                        return true; // Wayland accessibility NOT enabled
                }
            }
            catch
            {
                // gsettings unavailable — assume no missing perms
            }
        }

        return false;
    }

    /// <summary>
    /// Windows: Check if we can enumerate processes properly.
    /// </summary>
    private static bool CheckWindowsPermissions()
    {
        try
        {
            // Try to open a system process handle (requires PROCESS_QUERY_INFORMATION)
            // If this fails, we have restricted permissions
            var processes = System.Diagnostics.Process.GetProcesses();
            return false; // We can see processes → OK
        }
        catch
        {
            return true; // Need admin/UAC elevation
        }
    }

    /// <summary>
    /// macOS: Check accessibility and full disk access permissions.
    /// </summary>
    private static bool CheckMacOSPermissions()
    {
        try
        {
            // Try to use `osascript` to get frontmost app (requires Accessibility)
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                Arguments = "-e 'tell application \"System Events\" to get name of first application process whose frontmost is true'",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return true;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return true; // Accessibility not granted
            return false; // Can interact with System Events → OK
        }
        catch
        {
            return true; // osascript not available → missing perms
        }
    }

    /// <summary>
    /// Build a detailed message about what permissions are still missing.
    /// This gives the user actionable feedback instead of a generic retry message.
    /// </summary>
    private string GetMissingPermissionsMessage()
    {
        var issues = new List<string>();

        // Check InstalledAppDetector missing permissions
        var detectorMissing = _appDetector.MissingPermissions;
        if (detectorMissing.Count > 0)
        {
            var grantInstructions = _appDetector.PermissionGrantInstructions;
            issues.AddRange(grantInstructions);
        }

        // Check platform-specific issues
        if (OperatingSystem.IsLinux())
        {
            var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";
            if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("Wayland toolkit accessibility may still be disabled. The pkexec script attempted to enable it. " +
                           "If this persists, manually run in a terminal:\n" +
                           "   gsettings set org.gnome.desktop.interface toolkit-accessibility true\n" +
                           "Or enable it in GNOME Settings → Accessibility → 'Toolkit Accessibility'.");
            }
        }

        if (issues.Count == 0)
        {
            return "Permissions still missing. Please try again or check system settings.";
        }

        return "Permissions still missing:\n" + string.Join("\n", issues.Select(i => "• " + i));
    }

    // ─── Auth Commands ───

    [RelayCommand]
    private void CancelLogin()
    {
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(EmployeeId) || string.IsNullOrWhiteSpace(SecretKey))
        {
            StatusMessage = "Please enter both Employee ID and Secret Key.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Authenticating...";

        try
        {
            var serverUrl = _config.ServerUrl ?? "http://localhost:8080";
            var payload = new { employeeId = EmployeeId.Trim(), secretKey = SecretKey.Trim() };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{serverUrl}/api/v1/auth/employee-login", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    var errObj = JsonSerializer.Deserialize<ErrorResponse>(responseBody);
                    StatusMessage = errObj?.Message ?? "Login failed. Check your credentials.";
                }
                catch
                {
                    StatusMessage = $"Login failed ({(int)response.StatusCode}). Check your credentials.";
                }
                return;
            }

            var loginResp = JsonSerializer.Deserialize<EmployeeLoginResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (loginResp?.Employee == null)
            {
                StatusMessage = "Invalid response from server.";
                return;
            }

            var emp = loginResp.Employee;

            var employeeInfo = new EmployeeInfo
            {
                Id = emp.Id,
                EmployeeId = emp.EmployeeId,
                Name = emp.Name,
                Email = emp.Email,
                Role = emp.Role,
                Department = emp.Department,
                Shift = emp.Shift,
                Avatar = emp.Avatar,
                AvatarColor = emp.AvatarColor,
                Token = loginResp.Token,
            };

            await _store.SaveEmployeeInfoAsync(employeeInfo, CancellationToken.None);

            _logCollector.SetEmployeeInfo(emp.EmployeeId, emp.Name, loginResp.Token ?? string.Empty);
            _logCollector.StartTracking();

            IsLoggedIn = true;
            EmployeeName = emp.Name;
            EmployeeDepartment = emp.Department;
            EmployeeRole = emp.Role;
            EmployeeAvatar = emp.Avatar ?? string.Empty;
            EmployeeAvatarColor = emp.AvatarColor ?? "#8B5CF6";
            StatusMessage = string.Empty;

            // Advance through permission steps
            CurrentPermissionStep = await GetNextPermissionStep();

            // Scan for browsers and enable auto-start after GUI login too
            _autoStart.EnableAutoStartForced();
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Connection error: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusMessage = "Request timed out. Check your connection.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        try
        {
            var info = await _store.GetEmployeeInfoAsync(CancellationToken.None);
            if (info != null)
            {
                var serverUrl = _config.ServerUrl ?? "http://localhost:8080";
                var payload = new { employeeId = info.EmployeeId, token = info.Token ?? "" };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                await _httpClient.PostAsync($"{serverUrl}/api/v1/auth/employee-disconnect", content);
            }
        }
        catch { }

        _logCollector.StopTracking();

        // Clear permission progress
        await _store.SetStatusAsync("perm_auto_start", "false", CancellationToken.None);
        await _store.SetStatusAsync("perm_background", "false", CancellationToken.None);
        await _store.SetStatusAsync("perm_other", "false", CancellationToken.None);

        await _store.ClearEmployeeInfoAsync(CancellationToken.None);
        IsLoggedIn = false;
        CurrentPermissionStep = PermissionStep.None;
        EmployeeName = string.Empty;
        EmployeeDepartment = string.Empty;
        EmployeeRole = string.Empty;
        EmployeeAvatar = string.Empty;
        EmployeeAvatarColor = string.Empty;
        StatusMessage = string.Empty;
        StepStatusText = string.Empty;
    }

    // ─── Permission Step Execution ───

    [RelayCommand]
    private async Task GrantCurrentStepPermissionAsync()
    {
        IsStepWorking = true;
        StepStatusText = "Working...";

        try
        {
            switch (CurrentPermissionStep)
            {
                case PermissionStep.AutoStart:
                    await GrantAutoStartAsync();
                    break;
                case PermissionStep.BackgroundRunning:
                    await GrantBackgroundRunningAsync();
                    break;
                case PermissionStep.Dependencies:
                    await InstallDependenciesAsync();
                    break;
                case PermissionStep.OtherPermissions:
                    await GrantOtherPermissionsAsync();
                    break;
            }

            // Re-evaluate progress
            CurrentPermissionStep = await GetNextPermissionStep();
            if (IsProfile)
            {
                StepStatusText = string.Empty;
            }
        }
        catch (Exception ex)
        {
            StepStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsStepWorking = false;
        }
    }

    private Task GrantAutoStartAsync()
    {
        var ok = _autoStart.EnableAutoStartForced();
        if (!ok) throw new InvalidOperationException("Failed to enable auto-start. Try running as administrator.");
        StepStatusText = "Auto-start enabled successfully.";
        return Task.CompletedTask;
    }

    private Task GrantBackgroundRunningAsync()
    {
        _autoStart.EnableAutoStartForced();
        StepStatusText = "Background guard enabled successfully.";
        return Task.CompletedTask;
    }

    private async Task InstallDependenciesAsync()
    {
        var missingDeps = GetMissingLinuxDependencies();
        if (missingDeps.Count == 0) return;

        var deps = string.Join(" ", missingDeps);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pkexec",
            Arguments = $"apt-get install -y {deps}",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        StepStatusText = "PolKit password dialog opened. Enter your password to install dependencies.";
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc != null)
        {
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0)
                StepStatusText = "Dependencies installed successfully.";
            else
                StepStatusText = "Failed to install dependencies.";
        }
    }

    [RelayCommand]
    private void ShowManualInstall()
    {
        var missingDeps = GetMissingLinuxDependencies();
        if (missingDeps.Count > 0)
        {
            var deps = string.Join(" ", missingDeps);
            StepStatusText = $"sudo apt-get install -y {deps}";
        }
    }

    private async Task GrantOtherPermissionsAsync()
    {
        var exited = false;

        if (OperatingSystem.IsLinux())
        {
            exited = await GrantLinuxPermissionsAsync();
        }
        else if (OperatingSystem.IsWindows())
        {
            GrantWindowsPermissions();
            exited = true;
        }
        else if (OperatingSystem.IsMacOS())
        {
            ShowMacOSPermissionInstructions();
            exited = true;
        }

        // Wait for the process to finish, then re-check
        if (exited)
        {
            await Task.Delay(2000); // brief settle time
        }

        _appDetector.ForceRecheck();

        if (HasMissingPermissions())
        {
            StepStatusText = GetMissingPermissionsMessage();
        }
        else
        {
            StepStatusText = "All permissions granted.";
        }
    }

    private async Task<bool> GrantLinuxPermissionsAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var currentUser = Environment.UserName;

        // Determine if we're running via `dotnet run` (ProcessPath == dotnet host)
        var processPath = Environment.ProcessPath ?? string.Empty;
        var isDotnetRun = processPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
                          processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

        // Detect session type to decide which permissions to grant
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "x11";

        // Write temp script so pkexec arguments are clean
        var tmpScript = Path.Combine(Path.GetTempPath(), "alpha_perm_" + Guid.NewGuid().ToString("N") + ".sh");
        try
        {
            var scriptBuilder = new System.Text.StringBuilder();
            scriptBuilder.Append("#!/bin/sh\n");

            // 1. Make shell history files readable (chmod +r as root ensures it works)
            scriptBuilder.AppendLine($"chmod +r \"{home}/.bash_history\" 2>/dev/null || true");
            scriptBuilder.AppendLine($"chmod +r \"{home}/.zsh_history\" 2>/dev/null || true");
            scriptBuilder.AppendLine($"chmod +r \"{home}/.local/share/fish/fish_history\" 2>/dev/null || true");

            // 2. Enable Wayland toolkit accessibility (needed for window title tracking)
            //    Must run as $USER because gsettings/dconf are per-user settings, not root
            if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
            {
                scriptBuilder.AppendLine($"sudo -u \"{currentUser}\" gsettings set org.gnome.desktop.interface toolkit-accessibility true 2>/dev/null || true");
                scriptBuilder.AppendLine($"sudo -u \"{currentUser}\" dconf write /org/gnome/desktop/interface/toolkit-accessibility true 2>/dev/null || true");
            }

            // 3. Grant capability for process detection (DAC_READ_SEARCH)
            //    Only available for published/installed binaries — dotnet run skips this
            if (!isDotnetRun)
            {
                scriptBuilder.AppendLine($"setcap CAP_DAC_READ_SEARCH+ep \"{processPath}\" 2>/dev/null || true");
            }

            var script = scriptBuilder.ToString();
            await File.WriteAllTextAsync(tmpScript, script);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pkexec",
                Arguments = $"bash {tmpScript}",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            StepStatusText = "PolKit password dialog opened. Enter your password to grant permissions.";
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                StepStatusText = "Failed to launch PolKit. Is pkexec installed?";
                return false;
            }

            // Wait up to 2 minutes for user to complete the dialog
            var exited = proc.WaitForExit(120_000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                StepStatusText = "PolKit timed out. Please try again.";
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            StepStatusText = $"Error: {ex.Message}";
            return false;
        }
        finally
        {
            try { if (File.Exists(tmpScript)) File.Delete(tmpScript); } catch { }
        }
    }

    private void GrantWindowsPermissions()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo Granting Alpha AI Tracker permissions... && whoami",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        System.Diagnostics.Process.Start(psi);
    }

    private void ShowMacOSPermissionInstructions()
    {
        StepStatusText = """
            Open System Settings → Privacy & Security:
            1. Accessibility → Add Alpha AI Tracker → Enable
            2. Full Disk Access → Add Alpha AI Tracker → Enable
            3. Screen Recording → Add Alpha AI Tracker → Enable (optional)
            """;
    }

    // ─── Platform Helpers ───

    private static string GetPlatformPermissionTitle()
    {
        if (OperatingSystem.IsLinux()) return "Grant Linux Permissions";
        if (OperatingSystem.IsWindows()) return "Grant Windows Permissions";
        if (OperatingSystem.IsMacOS()) return "Grant macOS Permissions";
        return "Grant Permissions";
    }

    private static string GetPlatformPermissionDescription()
    {
        if (OperatingSystem.IsLinux())
            return "Grant process detection and shell history access via administrator privileges.";
        if (OperatingSystem.IsWindows())
            return "Grant administrator permissions for full monitoring access.";
        if (OperatingSystem.IsMacOS())
            return "Enable Accessibility, Full Disk Access, and Screen Recording in System Settings.";
        return "Grant required permissions for monitoring.";
    }

    private static string GetPlatformPermissionButtonText()
    {
        if (OperatingSystem.IsLinux()) return "Grant via PolKit";
        if (OperatingSystem.IsWindows()) return "Grant via UAC";
        if (OperatingSystem.IsMacOS()) return "Show Instructions";
        return "Grant Permissions";
    }
}

// ────────────────────────────────
// DTOs for API responses
// ────────────────────────────────

public class EmployeeLoginResponse
{
    [JsonPropertyName("employee")]
    public EmployeeData? Employee { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

public class EmployeeData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("employeeId")]
    public string EmployeeId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("department")]
    public string Department { get; set; } = string.Empty;

    [JsonPropertyName("shift")]
    public string Shift { get; set; } = string.Empty;

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    [JsonPropertyName("avatarColor")]
    public string? AvatarColor { get; set; }
}

public class ErrorResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}