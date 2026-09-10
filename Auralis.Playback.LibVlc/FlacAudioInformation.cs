using System.Buffers.Binary;
using System.IO;

namespace Auralis.Services;

/// <summary>Reads bounded native FLAC metadata from a completed, seekable resource.
/// STREAMINFO supplies exact sample duration; encoded audio bytes exclude metadata/cover padding.</summary>
internal static class FlacAudioInformation
{
    internal static PlaybackAudioInformation? Read(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 42) return null;
        Span<byte> header = stackalloc byte[4];
        stream.ReadExactly(header);
        if (!header.SequenceEqual("fLaC"u8)) return null;
        stream.ReadExactly(header);
        var last = (header[0] & 128) != 0;
        if ((header[0] & 127) != 0 || Length(header) != 34) return null;
        Span<byte> info = stackalloc byte[34]; stream.ReadExactly(info);
        var packed = BinaryPrimitives.ReadUInt64BigEndian(info[10..18]);
        var rate = (int)(packed >> 44);
        var channels = (int)((packed >> 41) & 7) + 1;
        var bits = (int)((packed >> 36) & 31) + 1;
        var samples = packed & 0xFFFFFFFFF;
        if (rate == 0 || bits < 4 || samples == 0) return null;
        for (var count = 0; !last; count++)
        {
            if (count >= 128 || stream.Position > 64 * 1024 * 1024 || stream.Length - stream.Position < 4) return null;
            stream.ReadExactly(header); last = (header[0] & 128) != 0;
            var length = Length(header);
            if ((header[0] & 127) == 127 || length > stream.Length - stream.Position) return null;
            stream.Seek(length, SeekOrigin.Current);
        }
        var audioBytes = stream.Length - stream.Position;
        if (audioBytes <= 0) return null;
        var kbps = audioBytes * 8d * rate / samples / 1000d;
        if (!double.IsFinite(kbps) || kbps > int.MaxValue) return null;
        return new("FLAC", Math.Max(1, (int)Math.Round(kbps)), rate, bits, channels, true, true);
    }
    private static int Length(ReadOnlySpan<byte> bytes) => bytes[1] << 16 | bytes[2] << 8 | bytes[3];
}
