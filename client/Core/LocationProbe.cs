using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace client.Core;

/// <summary>
/// Cross-platform location probe (Phase 3 GPS, finalplan §16). Tries the OS geolocation
/// API first; falls back to IP geolocation (source=ip, coarse accuracy — never labelled gps).
/// </summary>
public static class LocationProbe
{
    public readonly record struct LocationFix(
        double Latitude,
        double Longitude,
        double? AccuracyM,
        double? AltitudeM,
        string Source,
        string? Address);

    public static async Task<LocationFix?> TryGetFixAsync(HttpClient http, CancellationToken ct)
    {
        LocationFix? fix = null;
        if (OperatingSystem.IsWindows())
            fix = await TryWindowsAsync(ct);
        else if (OperatingSystem.IsLinux())
            fix = await TryLinuxAsync(ct);
        else if (OperatingSystem.IsMacOS())
            fix = await TryMacOSAsync(ct);

        if (fix.HasValue)
            return fix;

        return await TryIpGeolocationAsync(http, ct);
    }

    // ── Windows: Windows.Devices.Geolocation via PowerShell WinRT bridge ──

    private static async Task<LocationFix?> TryWindowsAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;

        const string script = """
            try {
              Add-Type -AssemblyName System.Runtime.WindowsRuntime
              $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
                $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
              })[0]
              function Await($WinRtTask, $ResultType) {
                $netTask = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($WinRtTask))
                $netTask.Wait(-1) | Out-Null
                $netTask.Result
              }
              [Windows.Devices.Geolocation.Geolocator,Windows.Devices.Geolocation,ContentType=WindowsRuntime] | Out-Null
              $loc = New-Object Windows.Devices.Geolocation.Geolocator
              $loc.DesiredAccuracy = [Windows.Devices.Geolocation.PositionAccuracy]::Default
              $pos = Await ($loc.GetGeopositionAsync()) ([Windows.Devices.Geolocation.Geoposition])
              $c = $pos.Coordinate
              Write-Output ("{0},{1},{2},{3}" -f $c.Latitude, $c.Longitude, $c.Accuracy, $c.Altitude)
            } catch { exit 1 }
            """;

        return await Task.Run(() => ParseProbeOutput(RunPowerShell(script), "wifi"), ct);
    }

    // ── Linux: GeoClue2 Simple client via busctl (when installed) ──

    private static async Task<LocationFix?> TryLinuxAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return null;

        // One-shot read from GeoClue2 Simple — works when geoclue is running and permitted.
        const string script = """
            set -e
            CLIENT=$(busctl call --system org.freedesktop.GeoClue2 /org/freedesktop/GeoClue2/Manager org.freedesktop.GeoClue2.Manager GetClient s org.freedesktop.portal.Desktop 2>/dev/null | awk '{print $2}' | tr -d '"')
            [ -n "$CLIENT" ] || exit 1
            busctl set-property --system org.freedesktop.GeoClue2 "$CLIENT" org.freedesktop.GeoClue2.Client DesktopId s org.freedesktop.portal.Desktop 2>/dev/null || true
            busctl call --system org.freedesktop.GeoClue2 "$CLIENT" org.freedesktop.GeoClue2.Client Start 2>/dev/null || true
            sleep 2
            busctl get-property --system org.freedesktop.GeoClue2 "$CLIENT" org.freedesktop.GeoClue2.Client Location 2>/dev/null
            """;

        return await Task.Run<LocationFix?>(() =>
        {
            var output = RunBash(script);
            if (string.IsNullOrWhiteSpace(output)) return null;
            // Location property: (dd) d Latitude d Longitude d Accuracy ...
            var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i + 2 < parts.Length; i++)
            {
                if (parts[i] == "d" &&
                    double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
                    parts[i + 2] == "d" &&
                    i + 3 < parts.Length &&
                    double.TryParse(parts[i + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                {
                    double? acc = null;
                    if (i + 5 < parts.Length && parts[i + 4] == "d" &&
                        double.TryParse(parts[i + 5], NumberStyles.Float, CultureInfo.InvariantCulture, out var accuracy))
                        acc = accuracy;
                    return new LocationFix(lat, lon, acc, null, "wifi", null);
                }
            }
            return null;
        }, ct);
    }

    // ── macOS: CoreLocation not wired on net10.0 yet — IP fallback only ──

    private static Task<LocationFix?> TryMacOSAsync(CancellationToken ct) =>
        Task.FromResult<LocationFix?>(null);

    // ── IP geolocation fallback (all platforms) ──

    private static async Task<LocationFix?> TryIpGeolocationAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(
                "http://ip-api.com/json/?fields=status,lat,lon,city,regionName,country",
                ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.GetProperty("status").GetString() != "success") return null;

            var lat = root.GetProperty("lat").GetDouble();
            var lon = root.GetProperty("lon").GetDouble();
            var city = root.TryGetProperty("city", out var c) ? c.GetString() : "";
            var region = root.TryGetProperty("regionName", out var r) ? r.GetString() : "";
            var country = root.TryGetProperty("country", out var co) ? co.GetString() : "";
            var address = string.Join(", ", new[] { city, region, country }.Where(s => !string.IsNullOrWhiteSpace(s)));

            // IP fixes are coarse (~1–50 km); accuracy_m left null so the web can show "IP".
            return new LocationFix(lat, lon, null, null, "ip", string.IsNullOrWhiteSpace(address) ? null : address);
        }
        catch
        {
            return null;
        }
    }

    private static LocationFix? ParseProbeOutput(string? output, string source)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var line = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(line)) return null;

        var parts = line.Split(',');
        if (parts.Length < 2) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return null;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return null;

        double? acc = parts.Length > 2 &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var accuracy)
            ? accuracy : null;
        double? alt = parts.Length > 3 &&
            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var altitude)
            ? altitude : null;

        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return null;
        return new LocationFix(lat, lon, acc, alt, source, null);
    }

    private static string? RunPowerShell(string script)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command {QuoteForShell(script)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return ProcessFilter.RunProbe(psi, 30_000);
    }

    private static string? RunBash(string script)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c {QuoteForShell(script)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return ProcessFilter.RunProbe(psi, 15_000);
    }

    private static string QuoteForShell(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}
