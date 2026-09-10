using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Auralis.Services;

/// <summary>One bounded decoded picture and one latest JPEG, never a queued stream or disk cache.
/// Memory output lets the host compose video, danmaku and modal blur without HWND airspace.</summary>
internal sealed class CompositedVideoFrames : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _allocation, _pixels;
    private int _width, _height, _pitch;
    private byte[]? _jpeg;
    private long _lastEncoded;
    private bool _disposed;

    internal void Attach(MediaPlayer player)
    {
        player.SetVideoFormatCallbacks(Format, Cleanup);
        player.SetVideoCallbacks(Lock, Unlock, Display);
    }

    private uint Format(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        lock (_gate)
        {
            if (_disposed || width == 0 || height == 0) return 0;
            ReleaseBuffer();
            var scale = Math.Min(1d, 1280d / Math.Max(width, height));
            _width = Math.Max(1, (int)Math.Round(width * scale));
            _height = Math.Max(1, (int)Math.Round(height * scale));
            _pitch = (_width * 4 + 31) & ~31;
            var alignedLines = (_height + 31) & ~31;
            _allocation = Marshal.AllocHGlobal(_pitch * alignedLines + 31);
            _pixels = new IntPtr((_allocation.ToInt64() + 31) & ~31L);
            Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
            width = (uint)_width; height = (uint)_height;
            pitches = (uint)_pitch; lines = (uint)alignedLines;
            _jpeg = null; _lastEncoded = 0;
            return 1;
        }
    }

    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(_gate);
        Marshal.WriteIntPtr(planes, _pixels);
        return IntPtr.Zero;
    }

    private void Unlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        // A paused decoder can unlock its first picture without a display callback.
        try { if (_jpeg is null) Display(opaque, picture); }
        finally { Monitor.Exit(_gate); }
    }

    private void Display(IntPtr opaque, IntPtr picture)
    {
        // Never marshal a frame to the UI dispatcher; replace the latest picture in place.
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (_disposed || _pixels == IntPtr.Zero || (_jpeg is not null && Stopwatch.GetElapsedTime(_lastEncoded, now).TotalMilliseconds < 33)) return;
            try
            {
                using var bitmap = new Bitmap(_width, _height, _pitch, PixelFormat.Format32bppRgb, _pixels);
                using var output = new MemoryStream();
                bitmap.Save(output, ImageFormat.Jpeg);
                _jpeg = output.ToArray();
                _lastEncoded = now;
            }
            catch (Exception e) when (e is ExternalException or ArgumentException) { /* Keep the last complete picture. */ }
        }
    }

    internal byte[]? ReadLatest() { lock (_gate) return _jpeg; }
    internal void Clear() { lock (_gate) { _jpeg = null; _lastEncoded = 0; } }
    private void Cleanup(ref IntPtr opaque) { lock (_gate) ReleaseBuffer(); }
    private void ReleaseBuffer()
    {
        if (_allocation != IntPtr.Zero) Marshal.FreeHGlobal(_allocation);
        _allocation = _pixels = IntPtr.Zero;
    }
    // Owner stops/disposes LibVLC before disposing callback storage.
    public void Dispose() { lock (_gate) { _disposed = true; ReleaseBuffer(); _jpeg = null; } }
}
