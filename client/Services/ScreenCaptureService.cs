using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;

namespace client.Services;

/// <summary>
/// One physical/virtual display the capture service can target.
/// </summary>
public sealed class StreamMonitorInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsPrimary { get; init; }
}

/// <summary>
/// Captures a selected monitor to JPEG while streaming is active.
/// Windows: GDI CopyFromScreen over EnumDisplayMonitors bounds. Non-Windows: parked.
/// Frames are latest-wins via a capacity-1 channel — never blocks the tracker loops.
/// </summary>
public sealed class ScreenCaptureService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogger<ScreenCaptureService> _logger;
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly object _monitorGate = new();
    private List<StreamMonitorInfo> _monitors = new();
    private Rectangle _captureBounds = Rectangle.Empty;
    private int _selectedIndex;

    private volatile bool _streamActive;
    private bool _loggedUnavailable;

    /// <summary>
    /// Raw BGRA frame for WebRTC encode (width, height, pixels). Fired on the capture thread.
    /// </summary>
    public event Action<int, int, byte[]>? OnRawFrame;

    public ScreenCaptureService(AppConfig config, ILogger<ScreenCaptureService> logger)
    {
        _config = config;
        _logger = logger;
        StreamAvailable = OperatingSystem.IsWindows();
        if (OperatingSystem.IsWindows())
            RefreshMonitors();
    }

    /// <summary>True when this OS can capture (Windows desktop session).</summary>
    public bool StreamAvailable { get; private set; }

    public ChannelReader<byte[]> Frames => _frames.Reader;

    public IReadOnlyList<StreamMonitorInfo> GetMonitors()
    {
        lock (_monitorGate)
        {
            // Always re-scan — monitors can be plugged/unplugged after process start,
            // and a one-shot constructor scan can race the desktop session.
            RefreshMonitorsUnlocked();
            return _monitors.ToList();
        }
    }

    public int SelectedMonitorIndex
    {
        get { lock (_monitorGate) return _selectedIndex; }
    }

    public void SetStreamActive(bool active)
    {
        if (active && OperatingSystem.IsWindows())
            RefreshMonitors();
        _streamActive = active;
    }

    /// <summary>Switch capture target. Invalid index is ignored; returns the applied index.</summary>
    public int SetSelectedMonitor(int index)
    {
        if (!OperatingSystem.IsWindows())
            return 0;

        lock (_monitorGate)
        {
            RefreshMonitorsUnlocked();
            if (_monitors.Count == 0)
                return _selectedIndex;
            if (index < 0 || index >= _monitors.Count)
                return _selectedIndex;
            _selectedIndex = index;
            ApplySelectedBoundsUnlocked();
            _logger.LogInformation(
                "Screen capture target → monitor {Index} ({Name}) {W}x{H}",
                _selectedIndex, _monitors[_selectedIndex].Name,
                _captureBounds.Width, _captureBounds.Height);
            return _selectedIndex;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            StreamAvailable = false;
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                _logger.LogInformation("Screen capture parked — Windows-only in Phase 1/2");
            }
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) { }
            return;
        }

        StreamAvailable = true;
        RefreshMonitors();
        _logger.LogInformation(
            "Screen capture ready (fps={Fps}, maxWidth={MaxWidth}, quality={Q}, monitors={Count})",
            _config.StreamFps, _config.StreamMaxWidth, _config.StreamJpegQuality, GetMonitors().Count);

        var fps = Math.Clamp(_config.StreamFps, 1, 30);
        var interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        var adaptiveFps = fps;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_streamActive)
            {
                adaptiveFps = fps;
                interval = TimeSpan.FromMilliseconds(1000.0 / adaptiveFps);
                try
                {
                    await Task.Delay(200, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var frame = CaptureFrame();
                if (frame.Jpeg is { Length: > 0 })
                    _frames.Writer.TryWrite(frame.Jpeg);
                if (frame.Bgra is { Length: > 0 } && frame.Width > 0 && frame.Height > 0)
                {
                    try
                    {
                        OnRawFrame?.Invoke(frame.Width, frame.Height, frame.Bgra);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "OnRawFrame subscriber failed");
                    }
                }
            }
            catch (Exception ex)
            {
                StreamAvailable = false;
                _logger.LogWarning(ex, "Screen capture failed — reporting unavailable");
                _streamActive = false;
                try
                {
                    await Task.Delay(5000, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                StreamAvailable = OperatingSystem.IsWindows();
                RefreshMonitors();
                continue;
            }

            sw.Stop();
            var budget = interval.TotalMilliseconds * 0.8;
            if (sw.ElapsedMilliseconds > budget && adaptiveFps > 5)
            {
                adaptiveFps = 5;
                interval = TimeSpan.FromMilliseconds(1000.0 / adaptiveFps);
                _logger.LogDebug("Screen capture adaptive throttle → {Fps} fps", adaptiveFps);
            }

            var delay = interval - sw.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void RefreshMonitors()
    {
        lock (_monitorGate)
            RefreshMonitorsUnlocked();
    }

    private MonitorEnumProc? _enumProc; // keep alive across the native EnumDisplayMonitors call

    private void RefreshMonitorsUnlocked()
    {
#pragma warning disable CA1416
        var list = new List<(Rectangle Bounds, string Name, bool Primary)>();
        _enumProc = (IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr __) =>
        {
            // Prefer the rect the OS passes to the callback — always present even if
            // GetMonitorInfo fails on odd driver setups.
            var bounds = Rectangle.FromLTRB(lprc.Left, lprc.Top, lprc.Right, lprc.Bottom);
            var primary = false;
            var name = $"Display {list.Count + 1}";
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                if (!string.IsNullOrWhiteSpace(info.szDevice))
                    name = info.szDevice.Trim();
                var fromInfo = Rectangle.FromLTRB(
                    info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right, info.rcMonitor.Bottom);
                if (fromInfo.Width > 0 && fromInfo.Height > 0)
                    bounds = fromInfo;
            }
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return true;
            list.Add((bounds, name, primary));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, _enumProc, IntPtr.Zero);

        var reported = GetSystemMetrics(SM_CMONITORS);
        if (list.Count == 0)
        {
            var w = GetSystemMetrics(SM_CXSCREEN);
            var h = GetSystemMetrics(SM_CYSCREEN);
            if (w > 0 && h > 0)
                list.Add((new Rectangle(0, 0, w, h), "Primary", true));
        }
        else if (reported > list.Count)
        {
            _logger.LogWarning(
                "EnumDisplayMonitors returned {Found} display(s) but SM_CMONITORS={Reported}",
                list.Count, reported);
        }

        // Stable order: primary first, then left-to-right / top-to-bottom.
        list.Sort((a, b) =>
        {
            if (a.Primary != b.Primary) return a.Primary ? -1 : 1;
            var cmp = a.Bounds.Left.CompareTo(b.Bounds.Left);
            return cmp != 0 ? cmp : a.Bounds.Top.CompareTo(b.Bounds.Top);
        });

        // If nothing was marked primary, mark the first.
        if (list.Count > 0 && !list.Exists(m => m.Primary))
        {
            var first = list[0];
            list[0] = (first.Bounds, first.Name, true);
        }

        _monitors = list.Select((m, i) => new StreamMonitorInfo
        {
            Index = i,
            Name = m.Primary ? $"{FriendlyMonitorName(m.Name, i)} (Primary)" : FriendlyMonitorName(m.Name, i),
            Width = m.Bounds.Width,
            Height = m.Bounds.Height,
            IsPrimary = m.Primary,
        }).ToList();

        _boundsByIndex = list.Select(m => m.Bounds).ToList();

        if (_selectedIndex < 0 || _selectedIndex >= _monitors.Count)
            _selectedIndex = 0;
        ApplySelectedBoundsUnlocked();

        _logger.LogInformation(
            "Screen capture monitors refreshed: {Count} ({Summary})",
            _monitors.Count,
            string.Join(", ", _monitors.Select(m => $"{m.Index}:{m.Name} {m.Width}x{m.Height}")));
#pragma warning restore CA1416
    }

    private static string FriendlyMonitorName(string raw, int index)
    {
        // "\\.\DISPLAY2" → "Display 2"
        if (raw.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(raw.AsSpan(@"\\.\DISPLAY".Length), out var n))
            return $"Display {n}";
        if (string.IsNullOrWhiteSpace(raw))
            return $"Display {index + 1}";
        return raw;
    }

    private List<Rectangle> _boundsByIndex = new();

    private void ApplySelectedBoundsUnlocked()
    {
        if (_boundsByIndex.Count == 0)
        {
            _captureBounds = Rectangle.Empty;
            return;
        }
        if (_selectedIndex < 0 || _selectedIndex >= _boundsByIndex.Count)
            _selectedIndex = 0;
        _captureBounds = _boundsByIndex[_selectedIndex];
    }

    private readonly record struct CapturedFrame(byte[]? Jpeg, byte[]? Bgra, int Width, int Height);

    private CapturedFrame CaptureFrame()
    {
        if (!OperatingSystem.IsWindows())
            return default;

#pragma warning disable CA1416 // guarded by IsWindows above
        Rectangle bounds;
        lock (_monitorGate)
        {
            if (_captureBounds.IsEmpty)
                RefreshMonitorsUnlocked();
            bounds = _captureBounds;
        }
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return default;

        using var src = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(src))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, src.Size, CopyPixelOperation.SourceCopy);
        }

        var maxW = Math.Max(320, _config.StreamMaxWidth);
        Bitmap toEncode = src;
        Bitmap? scaled = null;
        try
        {
            if (src.Width > maxW)
            {
                var newH = (int)Math.Round(src.Height * (maxW / (double)src.Width));
                // Keep 32bpp so WebRTC gets BGRA without a second conversion.
                scaled = new Bitmap(maxW, Math.Max(1, newH), PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, scaled.Width, scaled.Height);
                toEncode = scaled;
            }

            byte[]? bgra = null;
            if (OnRawFrame is not null)
                bgra = BitmapToBgra(toEncode);

            using var ms = new MemoryStream();
            var quality = Math.Clamp(_config.StreamJpegQuality, 10, 95);
            var encoder = GetJpegEncoder();
            if (encoder is null)
            {
                toEncode.Save(ms, ImageFormat.Jpeg);
            }
            else
            {
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                toEncode.Save(ms, encoder, ep);
            }
            return new CapturedFrame(ms.ToArray(), bgra, toEncode.Width, toEncode.Height);
        }
        finally
        {
            scaled?.Dispose();
        }
#pragma warning restore CA1416
    }

    private static byte[] BitmapToBgra(Bitmap bmp)
    {
#pragma warning disable CA1416
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = Math.Abs(data.Stride) * data.Height;
            var buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);
            // System.Drawing 32bppArgb is BGRA in memory on little-endian Windows.
            if (data.Stride != bmp.Width * 4)
            {
                // Compact rows if stride has padding.
                var compact = new byte[bmp.Width * bmp.Height * 4];
                for (var y = 0; y < bmp.Height; y++)
                    Buffer.BlockCopy(buf, y * data.Stride, compact, y * bmp.Width * 4, bmp.Width * 4);
                return compact;
            }
            return buf;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
#pragma warning restore CA1416
    }

    private static ImageCodecInfo? GetJpegEncoder()
    {
#pragma warning disable CA1416
        return ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
#pragma warning restore CA1416
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_CMONITORS = 80;
    private const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
