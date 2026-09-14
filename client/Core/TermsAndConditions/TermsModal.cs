using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace client.Core.TermsAndConditions;

/// <summary>
/// Launches the T&amp;C acceptance modal. In GUI mode, creates a standalone Avalonia window.
/// In headless (--background) mode, uses platform-native dialogs. The modal is uncloseable
/// until the employee accepts or declines.
/// </summary>
public sealed class TermsModal
{
    private readonly ILogger<TermsModal> _logger;

    public TermsModal(ILogger<TermsModal> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Show the T&amp;C modal for a list of features. Blocks until all required features
    /// are accepted or any required feature is declined. Returns true if all required
    /// features were accepted; false if any required feature was declined.
    /// </summary>
    public async Task<bool> ShowAsync(IReadOnlyList<FeatureTermsEntry> features, CancellationToken ct)
    {
        foreach (var feature in features)
        {
            _logger.LogInformation("Showing T&C modal for {FeatureId} (required={Required})",
                feature.FeatureId, feature.IsRequired);

            bool accepted;
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                accepted = await ShowPlatformDialogAsync(feature, ct);
            }
            else
            {
                // Fallback: log and accept (shouldn't happen on supported platforms)
                _logger.LogWarning("Unsupported platform for T&C modal — accepting by default");
                accepted = true;
            }

            if (!accepted && feature.IsRequired)
            {
                _logger.LogWarning("Required T&C declined for {FeatureId} — blocking", feature.FeatureId);
                return false;
            }
        }

        return true;
    }

    private async Task<bool> ShowPlatformDialogAsync(FeatureTermsEntry feature, CancellationToken ct)
    {
        var termsFile = Path.Combine(Path.GetTempPath(), $"aat_terms_{feature.FeatureId}.txt");
        try
        {
            await File.WriteAllTextAsync(termsFile, BuildTermsText(feature), Encoding.UTF8, ct);

            if (OperatingSystem.IsWindows())
                return await ShowWindowsDialogAsync(feature, termsFile, ct);
            if (OperatingSystem.IsLinux())
                return await ShowLinuxDialogAsync(feature, termsFile, ct);
            if (OperatingSystem.IsMacOS())
                return await ShowMacDialogAsync(feature, termsFile, ct);

            return false;
        }
        finally
        {
            try { File.Delete(termsFile); } catch { /* best effort */ }
        }
    }

    private static string BuildTermsText(FeatureTermsEntry feature)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ {feature.DisplayName} ═══");
        sb.AppendLine($"Terms Version: {feature.TermsVersion}");
        sb.AppendLine();
        sb.AppendLine(feature.Description);
        sb.AppendLine();
        sb.AppendLine("─── Terms & Conditions ───");
        sb.AppendLine();
        sb.AppendLine(feature.TermsText);
        sb.AppendLine();
        if (feature.CanRevoke)
        {
            sb.AppendLine($"Note: You can revoke your acceptance later. Effect: {feature.RevokeEffect}");
        }
        else
        {
            sb.AppendLine("Note: This is required to use the application.");
        }
        return sb.ToString();
    }

    // ─── Windows: PowerShell-based dialog ───

    private async Task<bool> ShowWindowsDialogAsync(FeatureTermsEntry feature, string termsFile, CancellationToken ct)
    {
        try
        {
            // Use PowerShell with .NET Windows Forms — available on all Windows 10+ machines
            var termsPreview = feature.TermsText.Length > 200
                ? feature.TermsText[..200] + "..."
                : feature.TermsText;
            var escapedPreview = termsPreview.Replace("'", "''");

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"" +
                    $"Add-Type -AssemblyName System.Windows.Forms; " +
                    $"$msg = '{feature.DisplayName}\\n\\nTerms v{feature.TermsVersion}\\n\\n{escapedPreview}\\n\\n" +
                    (feature.IsRequired ? "This is required to use the application." : "You can decline and enable this later in Settings > Privacy.") +
                    "\\n\\nClick OK to accept'; " +
                    $"$result = [System.Windows.Forms.MessageBox]::Show($msg, 'Terms & Conditions', 'OKCancel', 'Information'); " +
                    $"exit ([int]($result -eq 'OK'))\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Windows T&C dialog failed");
            return false;
        }
    }

    // ─── Linux: zenity / kdialog fallback ───

    private async Task<bool> ShowLinuxDialogAsync(FeatureTermsEntry feature, string termsFile, CancellationToken ct)
    {
        // Try zenity first
        if (await CommandExistsAsync("zenity"))
        {
            return await RunLinuxDialogAsync(feature, termsFile, "zenity", ct);
        }

        // Try kdialog
        if (await CommandExistsAsync("kdialog"))
        {
            return await RunKdialogAsync(feature, termsFile, ct);
        }

        _logger.LogWarning("No T&C dialog available (zenity/kdialog not found) — declining for safety");
        return false;
    }

    private async Task<bool> RunLinuxDialogAsync(FeatureTermsEntry feature, string termsFile, string dialog, CancellationToken ct)
    {
        try
        {
            var checkbox = feature.IsRequired ? "" : "--checkbox=\"I accept the terms\"";
            var args = $"--text-info --filename=\"{termsFile}\" " +
                       $"--title=\"{feature.DisplayName} — Terms & Conditions\" " +
                       $"{checkbox} --width=600 --height=500";

            var psi = new ProcessStartInfo
            {
                FileName = dialog,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(psi)!;
            await process.WaitForExitAsync(ct);

            if (feature.IsRequired)
            {
                // zenity --text-info returns 0 on OK, 1 on Cancel
                return process.ExitCode == 0;
            }
            else
            {
                // With checkbox, return 0 = checkbox checked + OK
                return process.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Linux zenity T&C dialog failed");
            return false;
        }
    }

    private async Task<bool> RunKdialogAsync(FeatureTermsEntry feature, string termsFile, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "kdialog",
                Arguments = $"--textinfo \"{termsFile}\" " +
                            $"--title \"{feature.DisplayName} — Terms & Conditions\" " +
                            $"--checklist \"I accept the terms\" \"accept\" \"on\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(psi)!;
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Linux kdialog T&C dialog failed");
            return false;
        }
    }

    // ─── macOS: osascript ───

    private async Task<bool> ShowMacDialogAsync(FeatureTermsEntry feature, string termsFile, CancellationToken ct)
    {
        try
        {
            var termsContent = await File.ReadAllTextAsync(termsFile, ct);
            // Escape for AppleScript
            var escaped = termsContent.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e 'display dialog \"{escaped}\" with title \"{feature.DisplayName} — Terms & Conditions\" " +
                            $"buttons {{\"Decline\", \"Accept\"}} default button \"Accept\"'",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(psi)!;
            await process.WaitForExitAsync(ct);

            // osascript returns 0 and outputs "button returned:Accept" on accept
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            return output.Contains("Accept");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "macOS osascript T&C dialog failed");
            return false;
        }
    }

    private static async Task<bool> CommandExistsAsync(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
