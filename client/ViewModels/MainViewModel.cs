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
        AutoStartService autoStart)
    {
        _config = config;
        _store = store;
        _httpClient = httpClient;
        _appDetector = appDetector;
        _shellCollector = shellCollector;
        _autoStart = autoStart;
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
        }
        else
        {
            IsLoggedIn = false;
            ShowLoginForm = false;
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
    [RelayCommand]
    private void RequestWindowsPermissions()
    {
        try
        {
            // Open Windows settings for privacy & accessibility permissions
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:privacy-accessibility",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);

            // Also show where to grant Full Disk Access / Accessibility
            PermissionCommands = """
                To grant required permissions:

                1. Go to Settings → Privacy & Security → Accessibility
                   → Enable Alpha AI Tracker

                2. Go to Settings → Privacy & Security → App permissions
                   → Enable access to app info, location, and file system

                3. For best results, run as Administrator:
                   - Right-click the app → "Run as administrator"
                   - Or install via the installer which auto-elevates

                Run these commands to set auto-start manually if needed:
                reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v AlphaAITracker /t REG_SZ /d "\"{exe_path}\" --minimized" /f
                """.Replace("{exe_path}", Environment.ProcessPath ?? "AlphaAITracker.exe");
        }
        catch (Exception ex)
        {
            PermissionCommands = $"Failed to open settings: {ex.Message}";
        }

        ShowPermissionsPanel = true;
    }

    /// <summary>
    /// Linux: Show commands to grant permissions and install systemd service.
    /// </summary>
    [RelayCommand]
    private void ShowLinuxPermissionCommands()
    {
        var exePath = Environment.ProcessPath ?? "AlphaAITracker";
        var bashPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bash_history");
        var zshPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zsh_history");

        PermissionCommands = $"""
            ═══ Linux Permission Setup ═══

            1. GRANT PERMISSIONS:

            # Allow reading shell history files:
            chmod +r "{bashPath}" 2>/dev/null
            chmod +r "{zshPath}" 2>/dev/null

            # Allow running xprop/xdotool for window detection:
            sudo apt install x11-utils xdotool  # Debian/Ubuntu
            sudo dnf install xorg-x11-utils xdotool  # Fedora
            sudo pacman -S xorg-xprop xdotool  # Arch

            # Grant read access to /proc for process detection:
            sudo setcap CAP_DAC_READ_SEARCH+ep "{exePath}"

            # For GNOME Shell integration (requires extension):
            # Install: gnome-shell-extension-appindicator
            # Enable: User Themes, Window List

            2. SYSTEMD PERSISTENCE (auto-start):

            # The app will auto-register as a user systemd service.
            # Verify with:
            systemctl --user status alpha-ai-tracker.service

            # Enable manually if needed:
            systemctl --user enable alpha-ai-tracker.service
            systemctl --user start alpha-ai-tracker.service

            3. BACKGROUND RUNNING:

            # The app runs as a systemd user service with Restart=always.
            # To check if it's running:
            ps aux | grep AlphaAITracker
            """;

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
            _autoStart.EnableAutoStart();
            IsAutoStartEnabled = true;
            AutoStartStatusText = "Auto-start enabled";
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
