using System.IO;
using System.Text;
using System.Text.Json;
using Auralis.Models;
using Auralis.Platform.Abstractions;

namespace Auralis.Services;

public sealed record LyricsResponse(
    string TrackId,
    string Source,
    bool IsSynced,
    bool Instrumental,
    IReadOnlyList<LyricLine> Lines,
    string Message,
    string SelectedSource = "none",
    LyricsSourceAvailability? Availability = null);

public sealed record LyricsSourceAvailability(
    bool HasLocal,
    string? LocalSource,
    bool HasOnlineCache,
    bool HasOnline,
    bool OnlineChecked,
    string? OnlineSource);

public sealed record LyricsCacheInfo(
    string TrackId,
    bool HasCache,
    bool HasOverride,
    string? CacheSource,
    DateTime? UpdatedAt);

public sealed class LyricsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16LittleEndian = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf32LittleEndian = new UTF32Encoding(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidCharacters: true);
    private static readonly Encoding StrictUtf32BigEndian = new UTF32Encoding(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidCharacters: true);
    private static readonly Encoding StrictGb18030 = CreateStrictCodePageEncoding(54936);
    private static readonly Encoding StrictGbk = CreateStrictCodePageEncoding(936);
    private readonly Func<PlatformLyricsLookupRequest, CancellationToken, Task<PlatformLyricsLookupResult?>>? _onlineLookup;
    private readonly string _cacheDirectory;
    private readonly string _overrideDirectory;

    public LyricsService(string? cacheDirectory = null, Func<PlatformLyricsLookupRequest, CancellationToken, Task<PlatformLyricsLookupResult?>>? onlineLookup = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Auralis",
                "Lyrics");
        _overrideDirectory = Path.Combine(
            Path.GetDirectoryName(_cacheDirectory) ?? _cacheDirectory,
            "LyricsOverrides");

        _onlineLookup = onlineLookup;
    }

    public async Task<LyricsResponse> GetLyricsAsync(
        StoredTrack storedTrack,
        TrackInfo track,
        bool allowOnline,
        string preference,
        string sourceSelection,
        CancellationToken cancellationToken)
    {
        var cacheFirst = string.Equals(preference, "cacheFirst", StringComparison.OrdinalIgnoreCase);
        var localOnly = string.Equals(preference, "localOnly", StringComparison.OrdinalIgnoreCase);
        var selected = NormalizeSourceSelection(sourceSelection);

        var overridden = await TryReadOverrideAsync(track.Id, cancellationToken);
        var sidecar = overridden is null
            ? await TryReadSidecarAsync(storedTrack.Path, track, cancellationToken)
            : null;
        var embedded = overridden is null && sidecar is null
            ? await TryReadEmbeddedAsync(storedTrack.Path, track.Id, cancellationToken)
            : null;
        var local = overridden ?? sidecar ?? embedded;

        var cached = !localOnly
            ? await TryReadCacheAsync(track.Id, cancellationToken)
            : null;
        var availability = CreateAvailability(local, cached, onlineChecked: cached is not null);

        if (selected == "local")
        {
            return AttachAvailability(
                local ?? Empty(track.Id, "本地", "未找到逐歌曲指定歌词、同目录 LRC 或内嵌歌词", "local"),
                "local",
                availability);
        }

        if (selected == "online")
        {
            if (localOnly)
            {
                return AttachAvailability(
                    Empty(track.Id, "在线", "当前歌词来源偏好设置为仅本地歌词", "online"),
                    "online",
                    availability);
            }

            if (cached is not null)
            {
                return AttachAvailability(
                    cached with { Source = $"{cached.Source} · 已缓存" },
                    "online",
                    availability);
            }

            if (!allowOnline)
            {
                return AttachAvailability(
                    Empty(track.Id, "在线", "在线歌词匹配尚未启用", "online"),
                    "online",
                    availability);
            }

            var online = await TryGetOnlineAsync(track, cancellationToken);
            availability = CreateAvailability(
                local,
                online,
                onlineChecked: true,
                hasOnlineCache: online is not null && File.Exists(GetCachePath(track.Id)));
            return AttachAvailability(
                online ?? Empty(track.Id, "在线", "未找到匹配歌词，或在线歌词插件未安装", "online"),
                "online",
                availability);
        }

        // A manually selected lyric is still the highest-priority source in automatic mode.
        if (overridden is not null)
        {
            return AttachAvailability(overridden, "local", availability);
        }

        if (cacheFirst && !localOnly)
        {
            if (cached is not null)
            {
                return AttachAvailability(
                    cached with { Source = $"{cached.Source} · 已缓存" },
                    "online",
                    availability);
            }
        }

        if (local is not null)
        {
            return AttachAvailability(local, "local", availability);
        }

        if (!cacheFirst && !localOnly)
        {
            if (cached is not null)
            {
                return AttachAvailability(
                    cached with { Source = $"{cached.Source} · 已缓存" },
                    "online",
                    availability);
            }
        }

        if (!allowOnline || localOnly)
        {
            return AttachAvailability(
                Empty(
                    track.Id,
                    "本地",
                    localOnly
                        ? "未找到逐歌曲指定歌词、同目录 LRC 或内嵌歌词"
                        : "未找到本地歌词；可在设置中启用在线歌词",
                    "local"),
                "local",
                availability);
        }

        var fetched = await TryGetOnlineAsync(track, cancellationToken);
        availability = CreateAvailability(
            local,
            fetched,
            onlineChecked: true,
            hasOnlineCache: fetched is not null && File.Exists(GetCachePath(track.Id)));
        return AttachAvailability(
            fetched ?? Empty(track.Id, "在线", "未找到匹配歌词，或在线歌词插件未安装", "online"),
            "online",
            availability);
    }

    public Task<LyricsResponse> GetLyricsAsync(
        StoredTrack storedTrack,
        TrackInfo track,
        bool allowOnline,
        string preference,
        CancellationToken cancellationToken) =>
        GetLyricsAsync(storedTrack, track, allowOnline, preference, "auto", cancellationToken);

    public Task<LyricsResponse> GetLyricsAsync(
        StoredTrack storedTrack,
        TrackInfo track,
        bool allowOnline,
        CancellationToken cancellationToken) =>
        GetLyricsAsync(storedTrack, track, allowOnline, "localFirst", "auto", cancellationToken);

    public async Task SetOverrideAsync(string trackId, string sourcePath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_overrideDirectory);
        var destination = GetOverridePath(trackId);
        await using var source = File.OpenRead(sourcePath);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken);
    }

    public Task RemoveOverrideAsync(string trackId)
    {
        var path = GetOverridePath(trackId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    public Task ClearCacheAsync(string trackId)
    {
        var path = GetCachePath(trackId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    public async Task<LyricsCacheInfo> GetCacheInfoAsync(string trackId, CancellationToken cancellationToken = default)
    {
        var cachePath = GetCachePath(trackId);
        var overridePath = GetOverridePath(trackId);
        string? source = null;
        if (File.Exists(cachePath))
        {
            try
            {
                await using var stream = File.OpenRead(cachePath);
                source = (await JsonSerializer.DeserializeAsync<LyricsResponse>(stream, JsonOptions, cancellationToken))?.Source;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                source = null;
            }
        }

        var updatedAt = new[] { cachePath, overridePath }
            .Where(File.Exists)
            .Select(File.GetLastWriteTime)
            .Cast<DateTime?>()
            .OrderByDescending(value => value)
            .FirstOrDefault();
        return new LyricsCacheInfo(trackId, File.Exists(cachePath), File.Exists(overridePath), source, updatedAt);
    }

    private async Task<LyricsResponse?> TryReadOverrideAsync(string trackId, CancellationToken cancellationToken)
    {
        var path = GetOverridePath(trackId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var content = await ReadLocalLyricsTextAsync(path, cancellationToken);
            return CreateResponse(trackId, "本地歌词 · 已指定", content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private async Task<LyricsResponse?> TryReadSidecarAsync(
        string audioPath,
        TrackInfo track,
        CancellationToken cancellationToken)
    {
        var match = LocalLyricsLocator.Find(audioPath, track.Title, track.Artist);
        if (match is null)
        {
            return null;
        }

        try
        {
            var content = await ReadLocalLyricsTextAsync(match.Path, cancellationToken);
            return CreateResponse(track.Id, "本地 LRC", content, "local");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static async Task<LyricsResponse?> TryReadEmbeddedAsync(
        string audioPath,
        string trackId,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await Task.Run(
                () => AudioMetadataReader.Read(audioPath).Lyrics,
                cancellationToken);
            return CreateResponse(trackId, "内嵌歌词", content, "local");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private async Task<LyricsResponse?> TryReadCacheAsync(string trackId, CancellationToken cancellationToken)
    {
        var path = GetCachePath(trackId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var response = await JsonSerializer.DeserializeAsync<LyricsResponse>(stream, JsonOptions, cancellationToken);
            return response?.Lines.Count > 0 || response?.Instrumental == true ? response : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(LyricsResponse response, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var path = GetCachePath(response.TrackId);
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, response, JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A cache failure should never interrupt playback or lyric display.
        }
    }

    private async Task<LyricsResponse?> TryGetOnlineAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        if (_onlineLookup is null) return null;
        try
        {
            var lookup = await _onlineLookup(new(track.Title, track.Artist, track.Album, track.DurationSeconds), cancellationToken);
            if (lookup is null) return null;
            var response = lookup.Instrumental
                ? new LyricsResponse(track.Id, lookup.Source, false, true, [], "纯音乐，请欣赏", "online")
                : CreateResponse(track.Id, lookup.Source, lookup.Text, "online");
            if (response is not null) await WriteCacheAsync(response, cancellationToken);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; } // A missing or failed optional plugin cannot interrupt local playback.
    }

    private static LyricsResponse? CreateResponse(
        string trackId,
        string source,
        string? content,
        string selectedSource = "none")
    {
        var lines = LyricsParser.Parse(content ?? string.Empty);
        if (lines.Count == 0)
        {
            return null;
        }

        var synced = lines.Any(line => line.TimeSeconds.HasValue);
        return new LyricsResponse(
            trackId,
            source,
            synced,
            false,
            lines,
            synced ? "同步歌词" : "歌词",
            selectedSource);
    }

    private static async Task<string> ReadLocalLyricsTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return DecodeLocalLyricsText(bytes);
    }

    private static string DecodeLocalLyricsText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        // Check UTF-32 before UTF-16 because the little-endian UTF-32 preamble starts with FF FE.
        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            return StrictUtf32BigEndian.GetString(bytes[4..]);
        }
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            return StrictUtf32LittleEndian.GetString(bytes[4..]);
        }
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return StrictUtf8.GetString(bytes[3..]);
        }
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return StrictUtf16LittleEndian.GetString(bytes[2..]);
        }
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return StrictUtf16BigEndian.GetString(bytes[2..]);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Most legacy Chinese LRC files are GBK/CP936. GB18030 is tried first because it
            // is the modern superset, while the explicit CP936 fallback preserves older files.
        }

        try
        {
            return StrictGb18030.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return StrictGbk.GetString(bytes);
        }
    }

    private static Encoding CreateStrictCodePageEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private static LyricsResponse Empty(
        string trackId,
        string source,
        string message,
        string selectedSource = "none") =>
        new(trackId, source, false, false, [], message, selectedSource);

    private static string NormalizeSourceSelection(string? sourceSelection) =>
        sourceSelection?.Trim().ToLowerInvariant() switch
        {
            "local" => "local",
            "online" => "online",
            _ => "auto"
        };

    private static LyricsSourceAvailability CreateAvailability(
        LyricsResponse? local,
        LyricsResponse? online,
        bool onlineChecked,
        bool? hasOnlineCache = null) =>
        new(
            local is not null,
            local?.Source,
            hasOnlineCache ?? online is not null,
            online is not null,
            onlineChecked,
            online?.Source);

    private static LyricsResponse AttachAvailability(
        LyricsResponse response,
        string selectedSource,
        LyricsSourceAvailability availability) =>
        response with
        {
            SelectedSource = selectedSource,
            Availability = availability
        };

    private string GetCachePath(string trackId) => Path.Combine(_cacheDirectory, trackId + ".json");

    private string GetOverridePath(string trackId) => Path.Combine(_overrideDirectory, trackId + ".lrc");

    public void Dispose()
    {
        // Optional lookup lifetime belongs to the platform host.
    }
}
