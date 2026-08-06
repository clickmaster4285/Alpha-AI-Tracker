using Microsoft.Extensions.Logging;

namespace client.Core.BrowserAccessibility;

/// <summary>Selects the accessibility reader for the current platform.</summary>
public static class AccessibilityBrowserReaderFactory
{
    public static IAccessibilityBrowserReader Create(ILoggerFactory loggerFactory)
    {
        if (OperatingSystem.IsLinux())
            return new LinuxAtSpiBrowserReader(loggerFactory.CreateLogger<LinuxAtSpiBrowserReader>());
        if (OperatingSystem.IsWindows())
            return new WindowsUiaBrowserReader(loggerFactory.CreateLogger<WindowsUiaBrowserReader>());
        if (OperatingSystem.IsMacOS())
            return new MacOsAccessibilityBrowserReader(loggerFactory.CreateLogger<MacOsAccessibilityBrowserReader>());

        throw new PlatformNotSupportedException(
            $"Accessibility browser tracking is not supported on {Environment.OSVersion}");
    }
}
