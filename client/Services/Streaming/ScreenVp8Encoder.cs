using System.Runtime.InteropServices;
using SIPSorceryMedia.Abstractions;
using vpxmd;

namespace client.Services.Streaming;

/// <summary>
/// Screen-share VP8 encoder that actually honors TargetKbps.
/// SIPSorceryMedia.Encoders' <c>Vp8Codec.InitialiseEncoder</c> sets
/// <c>RcTargetBitrate</c> THEN calls <c>VpxCodecEncConfigDefault</c>, which
/// wipes it — so every stream ran at libvpx's ~256 kbps default and looked
/// blocky no matter what ALPHA_STREAM_MAX_BITRATE_KBPS said.
/// </summary>
public sealed class ScreenVp8Encoder : IDisposable
{
    // Soft deadline in µs. 1 = realtime (softest). Give screen encode time for sharp text.
    private const uint EncodeDeadlineUs = 100_000;
    private const int VpxEncoderAbiVersion = 23;
    private const int VpxEflagForceKf = 1;

    private readonly object _gate = new();
    private VpxCodecCtx? _ctx;
    private VpxImage? _img;
    private bool _imgAllocated;
    private uint _width;
    private uint _height;
    private uint _targetKbps;
    private uint _kfMaxDist;
    private uint _fps = 10;
    private long _pts;
    private bool _forceKeyFrame = true;
    private bool _disposed;

    public uint TargetKbps
    {
        get => _targetKbps;
        set
        {
            lock (_gate)
            {
                if (_targetKbps == value) return;
                _targetKbps = value;
                DisposeEncoderUnlocked();
                _forceKeyFrame = true;
            }
        }
    }

    /// <summary>Capture/encode FPS — drives libvpx timebase so TargetKbps is spent correctly.</summary>
    public uint Fps
    {
        get => _fps;
        set
        {
            lock (_gate)
            {
                var next = Math.Clamp(value, 1, 30);
                if (_fps == next) return;
                _fps = next;
                DisposeEncoderUnlocked();
                _forceKeyFrame = true;
            }
        }
    }

    /// <summary>Max distance between keyframes (frames). Default ~1s at 10 fps.</summary>
    public uint KeyframeMaxDistance
    {
        get => _kfMaxDist;
        set => _kfMaxDist = Math.Max(1, value);
    }

    public void ForceKeyFrame() => _forceKeyFrame = true;

    public byte[]? EncodeBgra(int width, int height, byte[] bgra)
    {
        if (width < 2 || height < 2 || bgra.Length < width * height * 4)
            return null;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_ctx is null || _width != (uint)width || _height != (uint)height)
                InitEncoderUnlocked((uint)width, (uint)height);

            var i420 = PixelConverter.ToI420(width, height, width * 4, bgra, VideoPixelFormatsEnum.Bgra);
            return EncodeI420Unlocked(i420);
        }
    }

    private void InitEncoderUnlocked(uint width, uint height)
    {
        DisposeEncoderUnlocked();

        _width = width;
        _height = height;
        _pts = 0;
        _ctx = new VpxCodecCtx();
        _img = new VpxImage();

        var cfg = new VpxCodecEncCfg();
        // MUST call default FIRST — then override bitrate/size (SIPSorcery bug did the opposite).
        var defRes = vpx_encoder.VpxCodecEncConfigDefault(vp8cx.VpxCodecVp8Cx(), cfg, 0);
        if (defRes != VpxCodecErrT.VPX_CODEC_OK)
            throw new InvalidOperationException($"VP8 ConfigDefault failed: {defRes}");

        cfg.GW = width;
        cfg.GH = height;
        // timebase 1/fps + duration 1 ⇒ libvpx budgets TargetKbps across real wall-clock fps.
        cfg.GTimebase.Num = 1;
        cfg.GTimebase.Den = (int)Math.Max(1, _fps);
        cfg.RcTargetBitrate = Math.Max(500, _targetKbps);
        // VBR spends bits on hard frames (UI text) instead of padding CBR on static P-frames.
        cfg.RcEndUsage = VpxRcMode.VPX_VBR;
        cfg.GLagInFrames = 0;
        cfg.GErrorResilient = 0;
        cfg.RcUndershootPct = 100;
        cfg.RcOvershootPct = 100;
        // Screen text: keep quantizer low so edges stay sharp.
        cfg.RcMinQuantizer = 2;
        cfg.RcMaxQuantizer = 36;
        cfg.KfMode = VpxKfMode.VPX_KF_AUTO;
        cfg.KfMinDist = 0;
        cfg.KfMaxDist = _kfMaxDist > 0 ? _kfMaxDist : Math.Max(1, _fps);

        var initRes = vpx_encoder.VpxCodecEncInitVer(
            _ctx, vp8cx.VpxCodecVp8Cx(), cfg, 0, VpxEncoderAbiVersion);
        if (initRes != VpxCodecErrT.VPX_CODEC_OK)
            throw new InvalidOperationException(
                $"VP8 EncInit failed: {vpx_codec.VpxCodecErrToString(initRes)}");

        _forceKeyFrame = true;
    }

    private byte[]? EncodeI420Unlocked(byte[] i420)
    {
        if (_ctx is null || _img is null)
            return null;

        if (!_imgAllocated)
        {
            VpxImage.VpxImgAlloc(_img, VpxImgFmt.VPX_IMG_FMT_I420, _width, _height, 1);
            _imgAllocated = true;
        }

        unsafe
        {
            fixed (byte* pFrame = i420)
            {
                VpxImage.VpxImgWrap(_img, VpxImgFmt.VPX_IMG_FMT_I420, _width, _height, 1, pFrame);
                var flags = _forceKeyFrame ? VpxEflagForceKf : 0;
                var pts = _pts++;
                // duration = 1 timebase tick (timebase is 1/fps) ⇒ one frame of budget.
                var encodeRes = vpx_encoder.VpxCodecEncode(
                    _ctx, _img, pts, 1, flags, EncodeDeadlineUs);
                if (encodeRes != VpxCodecErrT.VPX_CODEC_OK)
                    throw new InvalidOperationException(
                        $"VP8 encode failed: {vpx_codec.VpxCodecErrToString(encodeRes)}");

                if (_forceKeyFrame)
                    _forceKeyFrame = false;

                IntPtr iter = IntPtr.Zero;
                var pkt = vpx_encoder.VpxCodecGetCxData(_ctx, (void**)&iter);
                byte[]? encoded = null;
                while (pkt is not null)
                {
                    if (pkt.Kind == VpxCodecCxPktKind.VPX_CODEC_CX_FRAME_PKT)
                    {
                        encoded = new byte[pkt.data.Raw.Sz];
                        Marshal.Copy(pkt.data.Raw.Buf, encoded, 0, encoded.Length);
                    }
                    pkt = vpx_encoder.VpxCodecGetCxData(_ctx, (void**)&iter);
                }
                return encoded;
            }
        }
    }

    private void DisposeEncoderUnlocked()
    {
        if (_img is not null)
        {
            if (_imgAllocated)
                VpxImage.VpxImgFree(_img);
            _img.Dispose();
            _img = null;
            _imgAllocated = false;
        }
        if (_ctx is not null)
        {
            vpx_codec.VpxCodecDestroy(_ctx);
            _ctx.Dispose();
            _ctx = null;
        }
        _width = 0;
        _height = 0;
        _pts = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeEncoderUnlocked();
        }
    }
}
