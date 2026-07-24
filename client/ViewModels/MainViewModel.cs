using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client.Configuration;
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
    private readonly IShellCommandCollector _shellCollector;
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

    public enum PermissionStep { None, AutoStart, BackgroundRunning, OtherPermissions }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoStartStep))]
    [NotifyPropertyChangedFor(nameof(IsBackgroundStep))]
    [NotifyPropertyChangedFor(nameof(IsPermissionStep))]
    [NotifyPropertyChangedFor(nameof(IsProfile))]
    [NotifyPropertyChangedFor(nameof(RequiresPermissionAction))]
    [NotifyPropertyChangedFor(nameof(StepTitle))]
    [NotifyPropertyChangedFor(nameof(StepDescription))]
    [NotifyPropertyChangedFor(nameof(StepButtonText))]
    [NotifyPropertyChangedFor(nameof(CurrentPermissionStepNumber))]
    private PermissionStep _currentPermissionStep = PermissionStep.None;

    public int CurrentPermissionStepNumber => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => 1,
        PermissionStep.BackgroundRunning => 2,
        PermissionStep.OtherPermissions => 3,
        _ => 0
    };

    public bool IsAutoStartStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.AutoStart;
    public bool IsBackgroundStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.BackgroundRunning;
    public bool IsPermissionStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.OtherPermissions;
    public bool IsProfile => IsLoggedIn && CurrentPermissionStep == PermissionStep.None;
    public bool RequiresPermissionAction => IsLoggedIn && CurrentPermissionStep != PermissionStep.None;

    public string StepTitle => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => "Enable Auto-Start",
        PermissionStep.BackgroundRunning => "Enable Background Guard",
        PermissionStep.OtherPermissions => GetPlatformPermissionTitle(),
        _ => string.Empty
    };

    public string StepDescription => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => "Auto-start ensures tracking resumes automatically after a reboot or system restart.",
        PermissionStep.BackgroundRunning => "The background guard keeps the service alive even when the window is closed.",
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
        IShellCommandCollector shellCollector,
        AutoStartService autoStart,
        LogCollectorService logCollector)
    {
        _config = config;
        _store = store;
        _httpClient = httpClient;
        _appDetector = appDetector;
        _shellCollector = shellCollector;
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

            // Resume from previous permission progress
            CurrentPermissionStep = await GetNextPermissionStep();
            if (IsProfile)
            {
                _autoStart.EnableAutoStartForced();
            }
        }
        else
        {
            IsLoggedIn = false;
            CurrentPermissionStep = PermissionStep.None;
        }
    }

    private async Task<PermissionStep> GetNextPermissionStep()
    {
        var permAuto = await _store.GetStatusAsync("perm_auto_start", CancellationToken.None);
        var permBg = await _store.GetStatusAsync("perm_background", CancellationToken.None);
        var permOther = await _store.GetStatusAsync("perm_other", CancellationToken.None);

        if (permAuto != "true")
        {
            var autoOk = _autoStart.IsAutoStartEnabled();
            if (autoOk) await _store.SetStatusAsync("perm_auto_start", "true", CancellationToken.None);
            else return PermissionStep.AutoStart;
        }

        if (permBg != "true")
        {
            var bgOk = IsBackgroundGuardConfigured();
            if (bgOk) await _store.SetStatusAsync("perm_background", "true", CancellationToken.None);
            else return PermissionStep.BackgroundRunning;
        }

        if (permOther != "true")
        {
            var otherOk = !HasMissingPermissions();
            if (otherOk) await _store.SetStatusAsync("perm_other", "true", CancellationToken.None);
            else return PermissionStep.OtherPermissions;
        }

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

    private bool HasMissingPermissions()
    {
        // Accessing the members triggers initialization if needed
        var missing = _appDetector.MissingPermissions;
        var shellPerms = _shellCollector.GetAccessibleShells();
        var allAccessible = shellPerms.All(kvp => kvp.Value);
        return missing.Count > 0 || !allAccessible;
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
            StepStatusText = "Permissions still missing. Please retry.";
        }
        else
        {
            StepStatusText = "All permissions granted.";
        }
    }

    private async Task<bool> GrantLinuxPermissionsAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Determine if we're running via `dotnet run` (ProcessPath == dotnet host)
        var processPath = Environment.ProcessPath ?? string.Empty;
        var isDotnetRun = processPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
                          processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

        // Write temp script so pkexec arguments are clean
        var tmpScript = Path.Combine(Path.GetTempPath(), "alpha_perm_" + Guid.NewGuid().ToString("N") + ".sh");
        try
        {
            var scriptBuilder = new System.Text.StringBuilder();
            scriptBuilder.Append("#!/bin/sh\n");
            scriptBuilder.Append($"chmod +r \"{home}/.bash_history\" \"{home}/.zsh_history\" \"{home}/.local/share/fish/fish_history\" 2>/dev/null\n");

            if (!isDotnetRun)
            {
                // Only setcap when running as a published/installed binary
                scriptBuilder.Append($"setcap CAP_DAC_READ_SEARCH+ep \"{processPath}\"\n");
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

        var settingsPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ms-settings:privacy-accessibility",
            UseShellExecute = true
        };
        System.Diagnostics.Process.Start(settingsPsi);
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