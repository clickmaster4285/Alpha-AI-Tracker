using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client.Configuration;
using System.Collections.ObjectModel;
using client.Core;
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

    public enum PermissionStep { None, AutoStart, BackgroundRunning, Dependencies, OtherPermissions, BrowserExtension }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoStartStep))]
    [NotifyPropertyChangedFor(nameof(IsBackgroundStep))]
    [NotifyPropertyChangedFor(nameof(IsDependencyStep))]
    [NotifyPropertyChangedFor(nameof(IsPermissionStep))]
    [NotifyPropertyChangedFor(nameof(IsBrowserSetup))]
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
        PermissionStep.BrowserExtension => OperatingSystem.IsLinux() ? 5 : 4,
        _ => 0
    };

    public int TotalPermissionSteps => OperatingSystem.IsLinux() ? 5 : 4;

    public bool IsAutoStartStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.AutoStart;
    public bool IsBackgroundStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.BackgroundRunning;
    public bool IsDependencyStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.Dependencies;
    public bool IsPermissionStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.OtherPermissions;
    public bool IsBrowserSetup => IsLoggedIn && CurrentPermissionStep == PermissionStep.BrowserExtension;
    public bool IsProfile => IsLoggedIn && CurrentPermissionStep == PermissionStep.None;
    public bool RequiresPermissionAction => IsLoggedIn && CurrentPermissionStep != PermissionStep.None;

    public string StepTitle => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => "Enable Auto-Start",
        PermissionStep.BackgroundRunning => "Enable Background Guard",
        PermissionStep.Dependencies => "Install Required Dependencies",
        PermissionStep.OtherPermissions => GetPlatformPermissionTitle(),
        PermissionStep.BrowserExtension => "Browser Journey Tracking",
        _ => string.Empty
    };

    public string StepDescription => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => "Auto-start ensures tracking resumes automatically after a reboot or system restart.",
        PermissionStep.BackgroundRunning => "The background guard keeps the service alive even when the window is closed.",
        PermissionStep.Dependencies => "The following dependencies are missing and must be installed to grant permissions.",
        PermissionStep.OtherPermissions => GetPlatformPermissionDescription(),
        PermissionStep.BrowserExtension => "Install the browser extension to track URLs and navigation. New Chromium/Gecko browsers: Refresh then Install. New browser profiles: run Install / Setup All again (already-seeded profiles are skipped).",
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

    private readonly BrowserExtensionService _browserExt;

    // ─── Browser Extension State ───

    /// <summary>True while the browser wizard step should be considered incomplete.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresBrowserSetup))]
    [NotifyPropertyChangedFor(nameof(HasPendingSetup))]
    private bool _browserSetupPending = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstructions))]
    private string _extensionInstructionsTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstructions))]
    private string _extensionInstructions = string.Empty;

    public bool HasInstructions => !string.IsNullOrEmpty(ExtensionInstructions);

    public ObservableCollection<DetectedBrowser> DetectedBrowsers { get; } = new();
    public ObservableCollection<SetupChecklistItem> SetupChecklist { get; } = new();

    public bool RequiresBrowserSetup => IsLoggedIn && BrowserSetupPending &&
                                        (!_browserExt.IsAnyExtensionActive);
    public bool HasPendingSetup => SetupChecklist.Count > 0;

    public MainViewModel(
        AppConfig config,
        ILogStore store,
        HttpClient httpClient,
        IInstalledAppDetector appDetector,
        AutoStartService autoStart,
        LogCollectorService logCollector,
        BrowserExtensionService browserExt)
    {
        _config = config;
        _store = store;
        _httpClient = httpClient;
        _appDetector = appDetector;
        _autoStart = autoStart;
        _logCollector = logCollector;
        _browserExt = browserExt;

        // React to real heartbeat changes from NativeMessageService so the UI flips
        // to "Connected" the moment the first ping arrives (≈ 2s after launch),
        // not on the next manual refresh.
        _browserExt.ExtensionConnectionChanged += OnExtensionConnectionChanged;
    }

    private void OnExtensionConnectionChanged(string browserName, bool isActive)
    {
        // Marshal back to the UI thread — the timer fires on a ThreadPool thread.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DetectedBrowsers.Clear();
            foreach (var b in _browserExt.DetectedBrowsers) DetectedBrowsers.Add(b);
            OnPropertyChanged(nameof(RequiresBrowserSetup));
            RefreshSetupChecklist();
        });
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
            await _store.SetStatusAsync("perm_browser", "false", ct);
            BrowserSetupPending = true;

            // Forced auto-start — always ensure it's configured
            _autoStart.EnableAutoStartForced();

            // Scan browsers BEFORE choosing the next wizard step so BrowserExtension
            // is never skipped due to an empty detection list from a late scan.
            await ScanBrowsersAsync(ct);
            CurrentPermissionStep = await GetNextPermissionStep();
            RefreshSetupChecklist();
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
    /// Browser extension is a real wizard step (never an optional overlay).
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

        // Browser extension (final wizard step) — shown until connected or user Continues.
        if (DetectedBrowsers.Count == 0)
            await ScanBrowsersAsync(CancellationToken.None);

        if (_browserExt.IsAnyExtensionActive)
        {
            await _store.SetStatusAsync("perm_browser", "true", CancellationToken.None);
            BrowserSetupPending = false;
            return PermissionStep.None;
        }

        if (BrowserSetupPending)
            return PermissionStep.BrowserExtension;

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
    /// - ShellCommandCollector (shell history file access)
    /// - Direct OS permission checks (/proc access on Linux, etc.)
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

            // Advance through permission steps (scan browsers first)
            _autoStart.EnableAutoStartForced();
            BrowserSetupPending = true;
            await _store.SetStatusAsync("perm_browser", "false", CancellationToken.None);
            await ScanBrowsersAsync(CancellationToken.None);
            CurrentPermissionStep = await GetNextPermissionStep();
            RefreshSetupChecklist();
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
        await _store.SetStatusAsync("perm_browser", "false", CancellationToken.None);
        BrowserSetupPending = true;
        SetupChecklist.Clear();

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
            RefreshSetupChecklist();
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
    private void ShowManualInstallCommand()
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

    // ─── Browser Extension Commands ───

    private async Task ScanBrowsersAsync(CancellationToken ct)
    {
        try
        {
            await _browserExt.ScanAsync(ct);

            DetectedBrowsers.Clear();
            foreach (var b in _browserExt.DetectedBrowsers)
                DetectedBrowsers.Add(b);

            if (_browserExt.IsAnyExtensionActive)
            {
                BrowserSetupPending = false;
                await _store.SetStatusAsync("perm_browser", "true", CancellationToken.None);
            }

            StepStatusText = string.Empty;
            OnPropertyChanged(nameof(RequiresBrowserSetup));
            RefreshSetupChecklist();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Browser scan failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AddExtensionToBrowserAsync(DetectedBrowser browser)
    {
        if (browser == null || !browser.CanInstall) return;

        IsStepWorking = true;
        ClearInstructions();

        // Set the intermediate "Connecting…" state. We do NOT mark the browser as
        // ExtensionActive until the heartbeat actually arrives — the BrowserExtensionService
        // has a 2s timer that polls NativeMessageService.IsExtensionConnected() and fires
        // ExtensionConnectionChanged when the state flips. Until then, the user sees the
        // truthful "Connecting…" badge instead of a green "Connected" lie.
        browser.Status = BrowserInstallStatus.Loading;

        try
        {
            // Step 1: Install native host manifests if not done
            if (!browser.NativeHostInstalled)
            {
                ExtensionInstructionsTitle = "Installing Native Messaging Host...";
                ExtensionInstructions = "Setting up the communication bridge between the browser and tracker...";

                var hostOk = await _browserExt.InstallNativeHostAsync(CancellationToken.None);
                if (!hostOk)
                {
                    ExtensionInstructionsTitle = "⚠ Installation Failed";
                    ExtensionInstructions = "Could not install the native messaging host automatically.\n\nThe host is the tracker executable itself (pure C#) — ensure the app has write access to your browser's NativeMessagingHosts directory.";
                    // Roll back — install actually failed.
                    browser.Status = BrowserInstallStatus.ReadyToInstall;
                    browser.NativeHostInstalled = false;
                    return;
                }
                browser.NativeHostInstalled = true;
            }

            // Step 2: Launch browser with extension (engine ladder — no brand names).
            // Chromium: CRX policy when configured, else --load-extension / Preferences.
            // Gecko: launch (+ temporary add-on if unsigned).
            if (browser.Engine is BrowserEngine.Chromium or BrowserEngine.Gecko)
            {
                ExtensionInstructionsTitle = $"Attaching to {browser.Name}…";
                ExtensionInstructions =
                    browser.Engine == BrowserEngine.Chromium
                        ? "Seeding every browser profile with --load-extension (this enables Developer mode the official way). " +
                          "Chrome/Edge will open and close once per profile — do not interrupt. " +
                          "Takes ~10s × number of profiles."
                        : "Installing the Gecko engine pack…";

                var result = await _browserExt.AttachExtensionAsync(browser);
                if (result.Success)
                {
                    ExtensionInstructionsTitle = result.WasRestarted
                        ? "🔄 Browser Restarted"
                        : "✅ Extension Load Requested";
                    ExtensionInstructions =
                        string.IsNullOrWhiteSpace(result.Message)
                            ? $"{browser.Name} launched with the tracking extension. " +
                              "Status flips to Connected within ~30s once the heartbeat arrives."
                            : result.Message;

                    if (browser.Engine == BrowserEngine.Gecko &&
                        !string.IsNullOrWhiteSpace(result.Message) &&
                        result.Message.Contains("Temporary", StringComparison.OrdinalIgnoreCase))
                    {
                        ExtensionInstructionsTitle = $"Gecko — {browser.Name}";
                    }
                }
                else
                {
                    browser.Status = BrowserInstallStatus.NativeHostReady;
                    var step2 = "Enable \"Developer mode\" (toggle in top-right)";
                    var step3 = "Click \"Load unpacked\"";
                    ExtensionInstructionsTitle = "📋 Could Not Auto-Load — Manual Fallback";
                    ExtensionInstructions =
                        $"{result.Message}\n\n" +
                        (browser.Engine == BrowserEngine.Gecko
                            ? $"1. Open about:debugging#/runtime/this-firefox\n" +
                              $"2. Load Temporary Add-on…\n" +
                              $"3. Select:\n   {Path.Combine(browser.ExtensionDir, "manifest.json")}"
                            : $"1. Open chrome://extensions in {browser.Name}\n2. {step2}\n3. {step3}\n" +
                              $"4. Select:\n   {browser.ExtensionDir}");
                }
            }
            else if (browser.Engine == BrowserEngine.WebKit)
            {
                browser.Status = BrowserInstallStatus.NotSupported;
                ExtensionInstructionsTitle = "WebKit — Not Supported";
                ExtensionInstructions =
                    $"{browser.Name} uses the WebKit engine, which does not expose the " +
                    $"WebExtensions + Native Messaging bridge used for journey tracking.";
            }
            else
            {
                browser.Status = BrowserInstallStatus.NotSupported;
                ExtensionInstructionsTitle = "Engine Unknown";
                ExtensionInstructions =
                    $"{browser.Name} was detected, but its engine could not be identified " +
                    $"(no profile created yet).\n\n" +
                    $"Launch {browser.Name} once, then click Refresh — the engine will be " +
                    $"classified from its profile shape (Chromium / Gecko / WebKit).";
            }

            // No aggressive re-scan here. We deliberately do NOT rebuild DetectedBrowsers,
            // because every rebuild creates brand-new DetectedBrowser objects and would
            // overwrite the optimistic ExtensionActive state we just set (the heartbeat
            // from the extension takes ~27s to arrive, well past the 8s this loop used to
            // wait). The next full refresh (click "Refresh", or restart, or explicit
            // ScanBrowsersAsync call elsewhere) will validate the heartbeat and confirm.
        }
        catch (Exception ex)
        {
            // On any exception, roll back to NativeHostReady so the user can retry.
            browser.Status = BrowserInstallStatus.NativeHostReady;
            ExtensionInstructionsTitle = "⚠ Error";
            ExtensionInstructions = $"An error occurred: {ex.Message}";
        }
        finally
        {
            IsStepWorking = false;
        }
    }

    [RelayCommand]
    private async Task RefreshBrowsersAsync()
    {
        ClearInstructions();
        await ScanBrowsersAsync(CancellationToken.None);
        if (!_browserExt.IsAnyExtensionActive)
        {
            ExtensionInstructionsTitle = "Select a browser below";
            ExtensionInstructions = "Click \"Install Extension\" next to a browser. After installing a new browser or creating a new profile, click Refresh then Install / Setup All again — already-attached profiles are skipped.";
        }
        else
        {
            ExtensionInstructionsTitle = "✅ All Connected!";
            ExtensionInstructions = "All detected browsers are sending data to the tracker.";
        }
    }

    /// <summary>
    /// One-click setup for ALL detected browsers (Phase 3): fresh scan → install
    /// the native messaging host manifest per browser → attach per engine.
    /// Chromium: --load-extension then profile injection. Firefox/Gecko: host +
    /// launch + manual instructions (MVP). Engine=Unknown: host installed, manual
    /// instructions (fail safe, not fail silent). Statuses flip to "✅ Active"
    /// automatically within ~30s once a browser connects (heartbeat poller).
    /// </summary>
    [RelayCommand]
    private async Task OneClickSetupAllAsync()
    {
        if (IsStepWorking) return;
        IsStepWorking = true;
        ClearInstructions();

        try
        {
            // Fresh scan so statuses reflect reality before we act.
            await ScanBrowsersAsync(CancellationToken.None);

            var pending = _browserExt.DetectedBrowsers
                .Where(b => b.CanInstall)
                .ToList();

            if (pending.Count == 0)
            {
                ExtensionInstructionsTitle = "Nothing to install";
                ExtensionInstructions =
                    _browserExt.IsAnyExtensionActive
                        ? "Every supported browser already has the extension active (heartbeat confirmed)."
                        : "No Chromium/Gecko browsers are ready to install (WebKit engines are not supported).";
                return;
            }

            var results = new List<string>();
            var hasUnknownEngine = false;

            // Step 1 (once): native messaging host manifests for ALL detected
            // browsers — the writer is idempotent and always overwrites.
            var hostOk = await _browserExt.InstallNativeHostAsync(CancellationToken.None);

            foreach (var browser in pending)
            {
                browser.Status = BrowserInstallStatus.Loading;
                browser.NativeHostInstalled = hostOk;

                if (!hostOk)
                {
                    results.Add($"{browser.Name}: ⚠ native host install failed — see manual steps.");
                    browser.Status = BrowserInstallStatus.ReadyToInstall;
                    continue;
                }

                // Step 2: engine-appropriate attach. Unknown → fail safe to manual.
                if (browser.Engine == BrowserEngine.Unknown)
                {
                    hasUnknownEngine = true;
                    results.Add($"{browser.Name}: ⚠ engine not auto-detected — native host installed. " +
                                $"Follow the manual steps below for {browser.Name}.");
                    browser.Status = BrowserInstallStatus.NativeHostReady;
                    continue;
                }

                // Step 2: engine-based attach (policy → load-extension → prefs / Gecko launch).
                var result = await _browserExt.AttachExtensionAsync(browser);
                if (result.Success)
                {
                    results.Add($"{browser.Name}: attached ({browser.EngineText}). {result.Message}".Trim());
                    browser.Status = BrowserInstallStatus.Loading;
                }
                else
                {
                    results.Add($"{browser.Name}: ⚠ {result.Message}");
                    browser.Status = BrowserInstallStatus.NativeHostReady;
                }
            }

            ExtensionInstructionsTitle = "✅ Setup Complete";
            var tail =
                "\n\nStatus flips to \"✅ Active\" automatically within ~30s once a browser " +
                "connects (heartbeat).";
            ExtensionInstructions = string.Join("\n\n", results) + tail;

            if (hasUnknownEngine)
            {
                var manualLines = pending
                    .Where(b => b.Engine == BrowserEngine.Unknown)
                    .Select(b => $"  • {b.Name}: open its extensions page → Load unpacked → {b.ExtensionDir}");
                ExtensionInstructions +=
                    "\n\nManual steps for unrecognized engines:" + "\n" + string.Join("\n", manualLines);
            }
        }
        catch (Exception ex)
        {
            ExtensionInstructionsTitle = "⚠ Error";
            ExtensionInstructions = $"One-click setup failed: {ex.Message}";
        }
        finally
        {
            IsStepWorking = false;
        }
    }

    [RelayCommand]
    private async void DismissBrowserSetup()
    {
        // User explicitly continues past the browser step (may install later from profile).
        BrowserSetupPending = false;
        await _store.SetStatusAsync("perm_browser", "true", CancellationToken.None);
        ClearInstructions();
        CurrentPermissionStep = await GetNextPermissionStep();
        RefreshSetupChecklist();
    }

    /// <summary>
    /// Rebuild the profile checklist of incomplete setup items (permissions + browsers).
    /// </summary>
    private void RefreshSetupChecklist()
    {
        SetupChecklist.Clear();

        if (!_autoStart.IsAutoStartEnabled())
        {
            SetupChecklist.Add(new SetupChecklistItem
            {
                Id = "auto_start",
                Title = "Auto-Start",
                Detail = "Tracking will not resume after reboot until auto-start is enabled.",
                ActionLabel = "Enable",
            });
        }

        if (!IsBackgroundGuardConfigured())
        {
            SetupChecklist.Add(new SetupChecklistItem
            {
                Id = "background",
                Title = "Background Guard",
                Detail = "Background guard is not configured.",
                ActionLabel = "Enable",
            });
        }

        if (OperatingSystem.IsLinux() && GetMissingLinuxDependencies().Count > 0)
        {
            SetupChecklist.Add(new SetupChecklistItem
            {
                Id = "dependencies",
                Title = "Missing Dependencies",
                Detail = string.Join(", ", GetMissingLinuxDependencies()),
                ActionLabel = "Install",
            });
        }

        if (HasMissingPermissions())
        {
            SetupChecklist.Add(new SetupChecklistItem
            {
                Id = "other",
                Title = GetPlatformPermissionTitle(),
                Detail = "Some OS permissions are still missing.",
                ActionLabel = "Grant",
            });
        }

        if (!_browserExt.IsAnyExtensionActive)
        {
            var pendingBrowsers = DetectedBrowsers.Count(b => b.CanInstall);
            SetupChecklist.Add(new SetupChecklistItem
            {
                Id = "browser",
                Title = "Browser Extension",
                Detail = DetectedBrowsers.Count == 0
                    ? "No browsers detected yet — click to re-scan and install."
                    : pendingBrowsers > 0
                        ? $"{pendingBrowsers} browser(s) ready to install the journey extension."
                        : "Extension is not connected (no heartbeat).",
                ActionLabel = "Open",
            });
        }

        OnPropertyChanged(nameof(HasPendingSetup));
    }

    [RelayCommand]
    private async Task FixSetupItemAsync(SetupChecklistItem? item)
    {
        if (item == null) return;

        switch (item.Id)
        {
            case "auto_start":
                CurrentPermissionStep = PermissionStep.AutoStart;
                break;
            case "background":
                CurrentPermissionStep = PermissionStep.BackgroundRunning;
                break;
            case "dependencies":
                CurrentPermissionStep = PermissionStep.Dependencies;
                break;
            case "other":
                CurrentPermissionStep = PermissionStep.OtherPermissions;
                break;
            case "browser":
                BrowserSetupPending = true;
                await _store.SetStatusAsync("perm_browser", "false", CancellationToken.None);
                await ScanBrowsersAsync(CancellationToken.None);
                CurrentPermissionStep = PermissionStep.BrowserExtension;
                break;
        }
    }

    private void ClearInstructions()
    {
        ExtensionInstructionsTitle = string.Empty;
        ExtensionInstructions = string.Empty;
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

public class SetupChecklistItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string ActionLabel { get; set; } = "Fix";
}

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