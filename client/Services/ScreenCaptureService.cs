using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;

namespace client.Services;

/// <summary>One physical/virtual display the capture service can target.</summary>
public sealed class StreamMonitorInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsPrimary { get; init; }
}

/// <summary>One BGRA screen frame for WebRTC encode (latest-wins channel).</summary>
public sealed class ScreenFrame
{
    public required byte[] Bgra { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long TimestampMs { get; init; }
}

/// <summary>
/// Captures a selected monitor to BGRA while streaming is active (Windows).
/// Frames are latest-wins via a capacity-1 channel — never blocks tracker loops.
/// Encoding to VP8/H264 is done by <see cref="LiveStreamClient"/> for WebRTC.
/// </summary>
public sealed class ScreenCaptureService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogger<ScreenCaptureService> _logger;
    private readonly Channel<ScreenFrame> _frames = Channel.CreateBounded<ScreenFrame>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly object _monitorGate = new();
    private List<StreamMonitorInfo> _monitors = new();
    private List<Rectangle> _boundsByIndex = new();
    private Rectangle _captureBounds = Rectangle.Empty;
    private int _selectedIndex;

    private volatile bool _streamActive;
    private bool _loggedUnavailable;
    private MonitorEnumProc? _enumProc;
    /// <summary>Runtime max width override from adaptive degradation (0 = use config).</summary>
    private volatile int _maxWidthOverride;
    private Bitmap? _srcBitmap;
    private Bitmap? _scaledBitmap;
    private byte[]? _bgraScratch;

    public ScreenCaptureService(AppConfig config, ILogger<ScreenCaptureService> logger)
    {
        _config = config;
        _logger = logger;
        StreamAvailable = OperatingSystem.IsWindows();
        if (OperatingSystem.IsWindows())
            RefreshMonitors();
    }

    public bool StreamAvailable { get; private set; }

    public ChannelReader<ScreenFrame> Frames => _frames.Reader;

    public IReadOnlyList<StreamMonitorInfo> GetMonitors()
    {
        lock (_monitorGate)
        {
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
        if (!active)
            _maxWidthOverride = 0;
        _streamActive = active;
    }

    /// <summary>
    /// Adaptive resolution lever (Phase 1). Pass 0 to clear and use ALPHA_STREAM_MAX_WIDTH.
    /// Values are clamped to [320, config max].
    /// </summary>
    public void SetMaxWidthOverride(int maxWidth)
    {
        if (maxWidth <= 0)
        {
            _maxWidthOverride = 0;
            return;
        }
        _maxWidthOverride = Math.Clamp(maxWidth, 320, Math.Max(320, _config.StreamMaxWidth));
    }

    public int EffectiveMaxWidth =>
        _maxWidthOverride > 0 ? _maxWidthOverride : Math.Max(320, _config.StreamMaxWidth);

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
        if (!_config.StreamEnabled)
        {
            _logger.LogInformation("Screen capture parked (ALPHA_STREAM_ENABLED=false)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            StreamAvailable = false;
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                _logger.LogInformation("Screen capture parked — Windows-only for WebRTC live stream");
            }
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        StreamAvailable = true;
        RefreshMonitors();
        _logger.LogInformation(
            "Screen capture ready (fps={Fps}, maxWidth={MaxWidth}, monitors={Count})",
            _config.StreamFps, _config.StreamMaxWidth, GetMonitors().Count);

        // Dedicated thread — GDI CopyFromScreen must not block the thread pool (F12/D8).
        await Task.Factory.StartNew(
            () => CaptureLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ConfigureAwait(false);
    }

    private void CaptureLoop(CancellationToken stoppingToken)
    {
        var fps = Math.Clamp(_config.StreamFps, 1, 30);
        var interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        var adaptiveFps = fps;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_streamActive)
            {
                adaptiveFps = fps;
                interval = TimeSpan.FromMilliseconds(1000.0 / adaptiveFps);
                try { Task.Delay(200, stoppingToken).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var frame = CaptureFrame();
                if (frame is not null)
                    _frames.Writer.TryWrite(frame);
            }
            catch (Exception ex)
            {
                StreamAvailable = false;
                _logger.LogWarning(ex, "Screen capture failed — reporting unavailable");
                _streamActive = false;
                try { Task.Delay(5000, stoppingToken).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
                StreamAvailable = OperatingSystem.IsWindows();
                RefreshMonitors();
                continue;
            }

            sw.Stop();
            // Don't tank quality by dropping to 5 fps under brief load — keep ≥8.
            // Network-driven adaptive FPS is applied by LiveStreamClient via SetMaxWidthOverride
            // and encoder Fps — this path only reacts to capture CPU cost.
            var budget = interval.TotalMilliseconds * 0.9;
            if (sw.ElapsedMilliseconds > budget && adaptiveFps > 8)
            {
                adaptiveFps = Math.Max(8, adaptiveFps - 2);
                interval = TimeSpan.FromMilliseconds(1000.0 / adaptiveFps);
                _logger.LogDebug("Screen capture adaptive throttle → {Fps} fps", adaptiveFps);
            }
            else if (sw.ElapsedMilliseconds < budget * 0.5 && adaptiveFps < fps)
            {
                adaptiveFps = Math.Min(fps, adaptiveFps + 1);
                interval = TimeSpan.FromMilliseconds(1000.0 / adaptiveFps);
            }

            var delay = interval - sw.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                try { Task.Delay(delay, stoppingToken).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void RefreshMonitors()
    {
        lock (_monitorGate)
            RefreshMonitorsUnlocked();
    }

    private void RefreshMonitorsUnlocked()
    {
#pragma warning disable CA1416
        var list = new List<(Rectangle Bounds, string Name, bool Primary)>();
        _enumProc = (IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr __) =>
        {
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

        list.Sort((a, b) =>
        {
            if (a.Primary != b.Primary) return a.Primary ? -1 : 1;
            var cmp = a.Bounds.Left.CompareTo(b.Bounds.Left);
            return cmp != 0 ? cmp : a.Bounds.Top.CompareTo(b.Bounds.Top);
        });

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
#pragma warning restore CA1416
    }

    private static string FriendlyMonitorName(string raw, int index)
    {
        if (raw.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(raw.AsSpan(@"\\.\DISPLAY".Length), out var n))
            return $"Display {n}";
        if (string.IsNullOrWhiteSpace(raw))
            return $"Display {index + 1}";
        return raw;
    }

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

    private ScreenFrame? CaptureFrame()
    {
        if (!OperatingSystem.IsWindows())
            return null;

#pragma warning disable CA1416
        Rectangle bounds;
        lock (_monitorGate)
        {
            if (_captureBounds.IsEmpty)
                RefreshMonitorsUnlocked();
            bounds = _captureBounds;
        }
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        if (_srcBitmap is null || _srcBitmap.Width != bounds.Width || _srcBitmap.Height != bounds.Height)
        {
            _srcBitmap?.Dispose();
            _srcBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        }

        using (var g = Graphics.FromImage(_srcBitmap))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, _srcBitmap.Size, CopyPixelOperation.SourceCopy);
        }

        var maxW = EffectiveMaxWidth;
        Bitmap toCopy = _srcBitmap;
        if (_srcBitmap.Width > maxW)
        {
            var newH = (int)Math.Round(_srcBitmap.Height * (maxW / (double)_srcBitmap.Width));
            newH = Math.Max(2, newH & ~1);
            var newW = maxW & ~1;
            if (_scaledBitmap is null || _scaledBitmap.Width != newW || _scaledBitmap.Height != newH)
            {
                _scaledBitmap?.Dispose();
                _scaledBitmap = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
            }
            using var g = Graphics.FromImage(_scaledBitmap);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.DrawImage(_srcBitmap, 0, 0, _scaledBitmap.Width, _scaledBitmap.Height);
            toCopy = _scaledBitmap;
        }

        var w = toCopy.Width & ~1;
        var h = toCopy.Height & ~1;
        if (w < 2 || h < 2)
            return null;

        var rect = new Rectangle(0, 0, w, h);
        var data = toCopy.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var needed = stride * h;
            if (_bgraScratch is null || _bgraScratch.Length < needed)
                _bgraScratch = new byte[needed];
            Marshal.Copy(data.Scan0, _bgraScratch, 0, needed);
            // Channel ownership requires a dedicated buffer — copy packed BGRA out.
            byte[] bgra;
            if (stride != w * 4)
            {
                bgra = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                    Buffer.BlockCopy(_bgraScratch, y * stride, bgra, y * w * 4, w * 4);
            }
            else
            {
                bgra = new byte[w * h * 4];
                Buffer.BlockCopy(_bgraScratch, 0, bgra, 0, w * h * 4);
            }

            return new ScreenFrame
            {
                Bgra = bgra,
                Width = w,
                Height = h,
                TimestampMs = Environment.TickCount64,
            };
        }
        finally
        {
            toCopy.UnlockBits(data);
        }
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
