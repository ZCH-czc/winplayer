using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Auralis.Services;

public sealed record AudioMetadata(
    string? Title,
    string? Artist,
    string? Album,
    byte[]? Picture,
    string? PictureExtension,
    double? DurationSeconds,
    string? Lyrics);

public static class AudioMetadataReader
{
    public static AudioMetadata Read(string path)
    {
        if (!Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            return new AudioMetadata(null, null, null, null, null, null, null);
        }

        try
        {
            return ReadFlac(path);
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or InvalidDataException)
        {
            return new AudioMetadata(null, null, null, null, null, null, null);
        }
    }

    private static AudioMetadata ReadFlac(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "fLaC")
        {
            throw new InvalidDataException("Not a FLAC stream.");
        }

        string? title = null;
        string? artist = null;
        string? album = null;
        byte[]? picture = null;
        string? pictureExtension = null;
        double? durationSeconds = null;
        string? lyrics = null;
        var isLast = false;

        while (!isLast)
        {
            var header = reader.ReadBytes(4);
            if (header.Length != 4)
            {
                throw new EndOfStreamException();
            }

            isLast = (header[0] & 0x80) != 0;
            var blockType = header[0] & 0x7f;
            var blockLength = (header[1] << 16) | (header[2] << 8) | header[3];
            var blockStart = stream.Position;

            if (blockType == 0 && blockLength >= 18)
            {
                var streamInfo = reader.ReadBytes(blockLength);
                var sampleRate = (streamInfo[10] << 12) | (streamInfo[11] << 4) | (streamInfo[12] >> 4);
                var totalSamples = ((long)(streamInfo[13] & 0x0f) << 32)
                    | ((long)streamInfo[14] << 24)
                    | ((long)streamInfo[15] << 16)
                    | ((long)streamInfo[16] << 8)
                    | streamInfo[17];
                if (sampleRate > 0 && totalSamples > 0)
                {
                    durationSeconds = totalSamples / (double)sampleRate;
                }
            }
            else if (blockType == 4)
            {
                var comments = ReadVorbisComments(reader, blockLength);
                comments.TryGetValue("TITLE", out title);
                comments.TryGetValue("ARTIST", out artist);
                comments.TryGetValue("ALBUM", out album);
                lyrics ??= FirstComment(
                    comments,
                    "SYNCEDLYRICS",
                    "LYRICS",
                    "UNSYNCEDLYRICS",
                    "UNSYNCED LYRICS",
                    "LYRIC");
            }
            else if (blockType == 6 && picture is null)
            {
                (picture, pictureExtension) = ReadPicture(reader, blockLength);
            }

            stream.Position = blockStart + blockLength;
        }

        return new AudioMetadata(title, artist, album, picture, pictureExtension, durationSeconds, lyrics);
    }

    private static string? FirstComment(
        IReadOnlyDictionary<string, string> comments,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (comments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static Dictionary<string, string> ReadVorbisComments(BinaryReader reader, int blockLength)
    {
        var end = reader.BaseStream.Position + blockLength;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var vendorLength = reader.ReadUInt32();
        reader.BaseStream.Position += vendorLength;
        var commentCount = reader.ReadUInt32();

        for (var index = 0u; index < commentCount && reader.BaseStream.Position + 4 <= end; index++)
        {
            var length = reader.ReadUInt32();
            if (length > end - reader.BaseStream.Position)
            {
                break;
            }

            var comment = Encoding.UTF8.GetString(reader.ReadBytes(checked((int)length)));
            var separator = comment.IndexOf('=');
            if (separator > 0)
            {
                var key = comment[..separator];
                var value = comment[(separator + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(value) && !result.ContainsKey(key))
                {
                    result[key] = value;
                }
            }
        }

        return result;
    }

    private static (byte[]? Data, string? Extension) ReadPicture(BinaryReader reader, int blockLength)
    {
        var data = reader.ReadBytes(blockLength);
        using var stream = new MemoryStream(data, writable: false);
        using var pictureReader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        _ = ReadUInt32BigEndian(pictureReader); // Picture type; the first embedded image is the fallback cover.
        var mimeLength = checked((int)ReadUInt32BigEndian(pictureReader));
        var mime = Encoding.ASCII.GetString(pictureReader.ReadBytes(mimeLength));
        var descriptionLength = checked((int)ReadUInt32BigEndian(pictureReader));
        pictureReader.BaseStream.Position += descriptionLength;
        pictureReader.BaseStream.Position += 16; // Width, height, color depth and indexed-color count.
        var imageLength = checked((int)ReadUInt32BigEndian(pictureReader));
        if (imageLength <= 0 || imageLength > pictureReader.BaseStream.Length - pictureReader.BaseStream.Position)
        {
            return (null, null);
        }

        var extension = mime.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg"
        };
        return (pictureReader.ReadBytes(imageLength), extension);
    }

    private static uint ReadUInt32BigEndian(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (reader.Read(bytes) != bytes.Length)
        {
            throw new EndOfStreamException();
        }

        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}
