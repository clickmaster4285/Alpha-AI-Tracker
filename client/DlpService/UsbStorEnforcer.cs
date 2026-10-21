using Microsoft.Win32;

namespace AlphaAITracker.Service;

/// <summary>
/// Coarse Windows USB mass-storage gate (P1): toggles USBSTOR service Start value.
///   3 = MANUAL (storage allowed) · 4 = DISABLED (mass-storage blocked).
/// Requires elevation / LocalSystem. Does not block HID keyboards/mice.
/// </summary>
internal static class UsbStorEnforcer
{
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\USBSTOR";
    public const int StartManual = 3;
    public const int StartDisabled = 4;

    public sealed record Result(bool Changed, int? PreviousStart, int DesiredStart, string Detail);

    public static Result Apply(bool block)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, null, block ? StartDisabled : StartManual, "not_windows");

        var desired = block ? StartDisabled : StartManual;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: true);
            if (key == null)
                return new Result(false, null, desired, "usbstor_key_missing");

            var prevObj = key.GetValue("Start");
            var prev = prevObj is int i ? i : (prevObj is long l ? (int)l : StartManual);
            if (prev == desired)
                return new Result(false, prev, desired, "already_applied");

            key.SetValue("Start", desired, RegistryValueKind.DWord);
            return new Result(true, prev, desired, block ? "usbstor_disabled" : "usbstor_enabled");
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
            || ex is System.Security.SecurityException
            || ex.Message.Contains("access is not allowed", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return new Result(false, null, desired, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, null, desired, "error:" + ex.Message);
        }
    }

    public static int? ReadCurrentStart()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: false);
            var v = key?.GetValue("Start");
            return v is int i ? i : v is long l ? (int)l : null;
        }
        catch
        {
            return null;
        }
    }
}
