using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using Microsoft.Extensions.Logging;

namespace client.Core.BrowserAccessibility;

/// <summary>
/// Windows implementation of <see cref="IAccessibilityBrowserReader"/> using UI Automation
/// (the built-in UIAutomationCore COM API via the Interop.UIAutomationClient wrapper).
///
/// Walks top-level windows, keeps browser-owned ones, and reads the address bar through
/// the ValuePattern of the "Address and search bar" edit control (Chrome/Edge expose it;
/// Firefox exposes its address field the same way). Works on every browser and every
/// Chrome version — no debugger, no extension.
///
/// Property/pattern ids are the stable UIA contract constants (UIAutomationClient.h).
/// </summary>
public sealed class WindowsUiaBrowserReader : IAccessibilityBrowserReader
{
    private const int UIA_ProcessIdPropertyId = 30002;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_NamePropertyId = 30005;
    private const int UIA_AutomationIdPropertyId = 30011;
    private const int UIA_NativeWindowHandlePropertyId = 30020;
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_WindowControlTypeId = 50032;
    private const int UIA_EditControlTypeId = 50004;

    private readonly ILogger<WindowsUiaBrowserReader> _logger;

    public string Platform => "Windows";
    public bool IsAvailable => OperatingSystem.IsWindows();

    public WindowsUiaBrowserReader(ILogger<WindowsUiaBrowserReader> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<AccessibilitySnapshot>> ReadAsync(CancellationToken ct)
    {
        var result = new List<AccessibilitySnapshot>();
        if (!OperatingSystem.IsWindows() || ct.IsCancellationRequested)
            return Task.FromResult<IReadOnlyList<AccessibilitySnapshot>>(result);

        CUIAutomationClass? automation = null;
        try
        {
            automation = new CUIAutomationClass();
            var uia = (IUIAutomation)automation;
            var root = uia.GetRootElement();
            var windowCondition = uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_WindowControlTypeId);
            var windows = root.FindAll(TreeScope.TreeScope_Children, windowCondition);

            for (var i = 0; i < windows.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var element = windows.GetElement(i);
                    var pid = element.CurrentProcessId;
                    var title = element.CurrentName ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(title) || pid <= 0)
                        continue;

                    string processName;
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        processName = proc.ProcessName;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!BrowserAccessibilityHelpers.IsBrowserProcess(processName))
                        continue;

                    result.Add(new AccessibilitySnapshot
                    {
                        WindowKey = $"uia:{pid}:{element.CurrentNativeWindowHandle.ToInt64()}",
                        ProcessId = pid,
                        ProcessName = processName,
                        WindowTitle = title,
                        Url = BrowserAccessibilityHelpers.NormalizeUrl(ReadAddressBar(element, uia)),
                        IsIncognito = BrowserAccessibilityHelpers.TitleSuggestsIncognito(title),
                    });
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UI Automation enumeration failed");
        }
        finally
        {
            if (automation is not null)
            {
                try
                {
                    if (automation is IDisposable disposable)
                        disposable.Dispose();
                    else
                        Marshal.ReleaseComObject(automation);
                }
                catch { }
            }
        }

        return Task.FromResult<IReadOnlyList<AccessibilitySnapshot>>(result);
    }

    private static string ReadAddressBar(IUIAutomationElement window, IUIAutomation uia)
    {
        try
        {
            var editCondition = uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_EditControlTypeId);
            var edits = window.FindAll(TreeScope.TreeScope_Descendants, editCondition);
            if (edits.Length == 0) return string.Empty;

            IUIAutomationElement? addressBar = null;
            for (var i = 0; i < edits.Length; i++)
            {
                try
                {
                    var edit = edits.GetElement(i);
                    var name = edit.CurrentName ?? string.Empty;
                    var automationId = edit.CurrentAutomationId ?? string.Empty;
                    if (name == "Address and search bar" || automationId == "Address and search bar" ||
                        name == "Search or enter address")
                    {
                        addressBar = edit;
                        break;
                    }
                    addressBar ??= edit;
                }
                catch { }
            }

            if (addressBar == null) return string.Empty;

            try
            {
                var pattern = addressBar.GetCurrentPattern(UIA_ValuePatternId);
                if (pattern is IUIAutomationValuePattern valuePattern)
                    return valuePattern.CurrentValue ?? string.Empty;
            }
            catch { }

            return addressBar.CurrentName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
