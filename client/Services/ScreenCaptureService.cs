using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;

namespace client.Services;

/// <summary>
/// Captures the primary screen to JPEG while streaming is active.
/// Windows: GDI CopyFromScreen (interactive desktop session). Non-Windows: parked.
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

    private volatile bool _streamActive;
    private bool _loggedUnavailable;

    public ScreenCaptureService(AppConfig config, ILogger<ScreenCaptureService> logger)
    {
        _config = config;
        _logger = logger;
        StreamAvailable = OperatingSystem.IsWindows();
    }

    /// <summary>True when this OS can capture (Windows desktop session).</summary>
    public bool StreamAvailable { get; private set; }

    public ChannelReader<byte[]> Frames => _frames.Reader;

    public void SetStreamActive(bool active) => _streamActive = active;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            StreamAvailable = false;
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                _logger.LogInformation("Screen capture parked — Windows-only in Phase 1");
            }
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) { }
            return;
        }

        StreamAvailable = true;
        _logger.LogInformation(
            "Screen capture ready (fps={Fps}, maxWidth={MaxWidth}, quality={Q})",
            _config.StreamFps, _config.StreamMaxWidth, _config.StreamJpegQuality);

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
                var jpeg = CaptureFrame();
                if (jpeg is { Length: > 0 })
                    _frames.Writer.TryWrite(jpeg);
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
            // else: skip sleep — over budget; next iteration is the "skip frame" path
        }
    }

    private byte[]? CaptureFrame()
    {
        if (!OperatingSystem.IsWindows())
            return null;

#pragma warning disable CA1416 // guarded by IsWindows above
        var screenW = GetSystemMetrics(0);
        var screenH = GetSystemMetrics(1);
        if (screenW <= 0 || screenH <= 0)
            return null;

        using var src = new Bitmap(screenW, screenH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(src))
        {
            g.CopyFromScreen(0, 0, 0, 0, src.Size, CopyPixelOperation.SourceCopy);
        }

        var maxW = Math.Max(320, _config.StreamMaxWidth);
        Bitmap toEncode = src;
        Bitmap? scaled = null;
        try
        {
            if (src.Width > maxW)
            {
                var newH = (int)Math.Round(src.Height * (maxW / (double)src.Width));
                scaled = new Bitmap(maxW, Math.Max(1, newH), PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, scaled.Width, scaled.Height);
                toEncode = scaled;
            }

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
            return ms.ToArray();
        }
        finally
        {
            scaled?.Dispose();
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

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
