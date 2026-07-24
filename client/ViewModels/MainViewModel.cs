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

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private bool _showLoginForm;

    [ObservableProperty]
    private string _employeeId = string.Empty;

    [ObservableProperty]
    private string _secretKey = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

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

    // ─── Permission & Background status ───

    [ObservableProperty]
    private bool _isAutoStartEnabled;

    [ObservableProperty]
    private bool _showPermissionsPanel;

    [ObservableProperty]
    private string _permissionInfo = string.Empty;

    [ObservableProperty]
    private string _permissionCommands = string.Empty;

    [ObservableProperty]
    private string _shellAccessInfo = string.Empty;

    [ObservableProperty]
    private string _autoStartStatusText = string.Empty;

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
            ShowLoginForm = false;
            EmployeeName = info.Name;
            EmployeeDepartment = info.Department;
            EmployeeRole = info.Role;
            EmployeeAvatar = info.Avatar ?? string.Empty;
            EmployeeAvatarColor = info.AvatarColor ?? "#7C3AED";

            // Resume tracking for previously logged-in session
            _logCollector.SetEmployeeInfo(info.EmployeeId, info.Name, info.Token ?? string.Empty);
            _logCollector.StartTracking();

            // Re-enforce auto-start
            _autoStart.EnableAutoStartForced();
            IsAutoStartEnabled = true;
            AutoStartStatusText = "Auto-start enabled (protected)";
        }
        else
        {
            IsLoggedIn = false;
            ShowLoginForm = true;
        }

        // Initialize permission & auto-start status
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        IsAutoStartEnabled = _autoStart.IsAutoStartEnabled();
        AutoStartStatusText = IsAutoStartEnabled ? "Auto-start enabled" : "Auto-start not enabled";

        // Build permission info
        var permLines = new List<string>();

        if (_appDetector.MissingPermissions.Count > 0)
        {
            permLines.Add("Missing App Detection Permissions:");
            permLines.AddRange(_appDetector.MissingPermissions);
            permLines.Add("");
            permLines.Add("To fix:");
            permLines.AddRange(_appDetector.PermissionGrantInstructions);
        }

        var shellPerms = _shellCollector.GetAccessibleShells();
        var accessibleCount = shellPerms.Count(kvp => kvp.Value);
        permLines.Add($"Shell histories accessible: {accessibleCount}/{shellPerms.Count}");

        if (_shellCollector.MissingPermissionInstructions.Count > 0)
        {
            permLines.Add("");
            permLines.Add("Shell Permission Instructions:");
            permLines.AddRange(_shellCollector.MissingPermissionInstructions);
        }

        PermissionInfo = string.Join("\n", permLines);
        ShellAccessInfo = string.Join(", ",
            shellPerms.Select(kvp => $"{kvp.Key}={(kvp.Value ? "✅" : "❌")}"));
    }

    [RelayCommand]
    private void ShowLogin()
    {
        ShowLoginForm = true;
        StatusMessage = string.Empty;
        EmployeeId = string.Empty;
        SecretKey = string.Empty;
    }

    [RelayCommand]
    private void CancelLogin()
    {
        ShowLoginForm = false;
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

            // Save to SQLite
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

            // Start tracking NOW — only after successful login
            _logCollector.SetEmployeeInfo(emp.EmployeeId, emp.Name, loginResp.Token ?? string.Empty);
            _logCollector.StartTracking();

            // Force-register auto-start (not deletable)
            _autoStart.EnableAutoStartForced();
            IsAutoStartEnabled = true;
            AutoStartStatusText = "Auto-start enabled (protected)";

            // Update UI
            IsLoggedIn = true;
            ShowLoginForm = false;
            EmployeeName = emp.Name;
            EmployeeDepartment = emp.Department;
            EmployeeRole = emp.Role;
            EmployeeAvatar = emp.Avatar ?? string.Empty;
            EmployeeAvatarColor = emp.AvatarColor ?? "#7C3AED";
            StatusMessage = string.Empty;
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
        // Notify server to untrack before clearing local data
        try
        {
            var info = await _store.GetEmployeeInfoAsync(CancellationToken.None);
            if (info != null)
            {
                var serverUrl = _config.ServerUrl ?? "http://localhost:8080";
                var payload = new { employeeId = info.EmployeeId, token = info.Token ?? "" };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                // Fire-and-forget — best effort to notify server
                var response = await _httpClient.PostAsync($"{serverUrl}/api/v1/auth/employee-disconnect", content);
            }
        }
        catch
        {
            // Best effort — don't block logout if server is unreachable
        }

        // Stop tracking immediately
        _logCollector.StopTracking();

        await _store.ClearEmployeeInfoAsync(CancellationToken.None);
        IsLoggedIn = false;
        ShowLoginForm = false;
        EmployeeName = string.Empty;
        EmployeeDepartment = string.Empty;
        EmployeeRole = string.Empty;
        EmployeeAvatar = string.Empty;
        EmployeeAvatarColor = string.Empty;
        StatusMessage = string.Empty;
    }

    // ─── Permission Commands ───

    [RelayCommand]
    private void TogglePermissionsPanel()
    {
        ShowPermissionsPanel = !ShowPermissionsPanel;
        if (ShowPermissionsPanel)
            RefreshStatus();
    }

    [RelayCommand]
    private void RefreshPermissionStatus()
    {
        RefreshStatus();
        StatusMessage = "Permission status refreshed.";
    }

    /// <summary>
    /// Windows: Open Windows Security/Accessibility settings.
    /// </summary>
    /// <summary>
    /// Windows: Request admin elevation via UAC popup.
    /// Uses a lightweight command to trigger UAC without re-launching the GUI app.
    /// </summary>
    [RelayCommand]
    private void RequestWindowsPermissions()
    {
        try
        {
            // Run a lightweight admin command to trigger UAC without spawning another GUI instance
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c echo Alpha AI Tracker is requesting admin permissions... && whoami",
                UseShellExecute = true,
                Verb = "runas",        // Triggers native UAC popup
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            System.Diagnostics.Process.Start(psi);

            // Also open the privacy settings page for accessibility permissions
            var settingsPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:privacy-accessibility",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(settingsPsi);

            PermissionCommands = """
                ✅ UAC elevation prompt was shown.
                Click "Yes" to grant administrator permissions.

                Also opened Windows Settings for Accessibility permissions.
                Enable Alpha AI Tracker in the list.

                This allows:
                • Reading all process and app usage data
                • Accessing shell/command history
                • Running persistently in the background
                """;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            PermissionCommands = "⚠️ UAC prompt was cancelled by user.\n\nClick the button again and accept the UAC prompt to grant permissions.";
        }
        catch (Exception ex)
        {
            PermissionCommands = $"Failed to open permissions: {ex.Message}";
        }

        ShowPermissionsPanel = true;
    }

    /// <summary>
    /// Linux: Request permissions via pkexec (PolKit) which shows a native GTK password dialog.
    /// User enters their admin password directly in the popup.
    /// </summary>
    [RelayCommand]
    private void ShowLinuxPermissionCommands()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? "AlphaAITracker";

            // Use pkexec to get root privileges — shows native PolKit password dialog
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pkexec",
                Arguments = $"chmod +r /home/*/.bash_history /home/*/.zsh_history /home/*/.local/share/fish/fish_history 2>/dev/null; setcap CAP_DAC_READ_SEARCH+ep \"{exePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(psi);

            PermissionCommands = """
                ✅ PolKit password prompt was shown.
                Enter your administrator password to grant:
                • Read access to shell history files
                • Process detection permissions
                • Full background persistence
                """;
        }
        catch (Exception ex)
        {
            PermissionCommands = $"Failed to launch pkexec: {ex.Message}\n\nMake sure 'pkexec' is installed (part of polkit).";
        }

        ShowPermissionsPanel = true;
    }

    /// <summary>
    /// macOS: Show commands to grant permissions and install launchd plist.
    /// </summary>
    [RelayCommand]
    private void ShowMacOSPermissionCommands()
    {
        PermissionCommands = """
            ═══ macOS Permission Setup ═══

            1. GRANT PERMISSIONS:

            Open System Settings → Privacy & Security:

            a) Accessibility:
               - Click the "+" button
               - Add Alpha AI Tracker from Applications
               - Toggle the switch ON

            b) Full Disk Access:
               - Click the "+" button
               - Add Alpha AI Tracker from Applications
               - Toggle the switch ON
               (Required for reading shell history files)

            c) Screen Recording (optional, for window titles):
               - Add Alpha AI Tracker
               - Toggle the switch ON

            2. AUTO-START:

            # The app creates a launchd plist at:
            ~/Library/LaunchAgents/com.alphaai.tracker.plist

            # Load it manually if needed:
            launchctl load ~/Library/LaunchAgents/com.alphaai.tracker.plist

            3. VERIFY:

            # Check if running:
            ps aux | grep AlphaAITracker

            # Check launchd status:
            launchctl list | grep com.alphaai.tracker
            """;

        ShowPermissionsPanel = true;
    }

    [RelayCommand]
    private void ToggleAutoStart()
    {
        if (IsAutoStartEnabled)
        {
            _autoStart.DisableAutoStart();
            IsAutoStartEnabled = false;
            AutoStartStatusText = "Auto-start disabled";
        }
        else
        {
            _autoStart.EnableAutoStartForced();
            IsAutoStartEnabled = true;
            AutoStartStatusText = "Auto-start enabled (protected)";
        }
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
