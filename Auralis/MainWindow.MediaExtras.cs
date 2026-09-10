using System.IO;
using System.Text.Json;
using Auralis.Services;

namespace Auralis;

public partial class MainWindow
{
    private readonly SavedPlaylistStore _savedPlaylistStore = new();
    private readonly Dictionary<string, CancellationTokenSource> _extrasRequests = new();

    private async Task HydrateSavedTrackAsync(JsonElement root)
    {
        var handle = JsonText(root, "handle");
        if (handle is not { Length: <= 128 } || !handle.StartsWith("saved-", StringComparison.Ordinal)) return;
        OnlineTrackView? item = null;
        try
        {
            var entry = (await _savedPlaylistStore.LoadAsync()).SelectMany(p => p.Entries)
                .FirstOrDefault(e => e.LocalId is null && "saved-" + e.Id == handle);
            if (entry is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                item = await OnlinePlatforms.RefreshSavedTrackAsync(entry, timeout.Token);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or InvalidOperationException)
        { /* Optional metadata never blocks playback or overwrites the user's saved reference. */ }
        await ExecuteScriptAsync($"window.Auralis?.setSavedTrackDetails({JsonSerializer.Serialize(new { handle, item }, WebJsonOptions)})");
    }

    private async Task RestoreSavedHandleAsync(string? handle)
    {
        if (handle is not { Length: <= 128 } || !handle.StartsWith("saved-", StringComparison.Ordinal) || OnlinePlatforms.GetBackendTrack(handle) is not null) return;
        try
        {
            var entry = (await _savedPlaylistStore.LoadAsync()).SelectMany(p => p.Entries)
                .FirstOrDefault(e => e.LocalId is null && "saved-" + e.Id == handle);
            if (entry is not null) OnlinePlatforms.RestoreSavedTrack(entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { /* Existing files are not overwritten. */ }
    }

    private void CancelPlatformExtras(string? kind = null)
    {
        if (kind is not null)
        {
            if (_extrasRequests.Remove(kind, out var request)) { request.Cancel(); request.Dispose(); }
            return;
        }
        foreach (var request in _extrasRequests.Values) { request.Cancel(); request.Dispose(); }
        _extrasRequests.Clear();
    }

    private async Task SendPlatformExtrasAsync(JsonElement root)
    {
        var handle = JsonText(root, "handle");
        var kind = JsonText(root, "kind");
        if (handle is not { Length: > 0 and <= 128 } || kind is not ("parts" or "danmaku" or "comments") ||
            !root.TryGetProperty("requestId", out var id) || !id.TryGetInt64(out var requestId)) return;
        await RestoreSavedHandleAsync(handle);
        if (_extrasRequests.Remove(kind, out var previous)) { previous.Cancel(); previous.Dispose(); }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cancellation.Token;
        _extrasRequests[kind] = cancellation;
        object items = Array.Empty<object>();
        object? error = null;
        string? nextPageHandle = null;
        try
        {
            if (kind == "parts")
            {
                var result = await OnlinePlatforms.GetPartsAsync(handle, token);
                if (result.IsSuccess) items = result.Value; else error = ToPlatformError(result.Error);
            }
            else if (kind == "danmaku")
            {
                var result = await OnlinePlatforms.GetDanmakuAsync(handle, token);
                if (result.IsSuccess) items = result.Value; else error = ToPlatformError(result.Error);
            }
            else
            {
                var result = await OnlinePlatforms.GetCommentsAsync(handle, JsonText(root, "pageHandle"), token);
                if (result.IsSuccess) { items = result.Value.Items; nextPageHandle = result.Value.NextPageHandle; }
                else error = ToPlatformError(result.Error);
            }
            // Providers can map cancellation into a typed result instead of throwing.
            // Still release the active UI request with a timeout message in that case.
            token.ThrowIfCancellationRequested();
            await SendExtrasResultAsync(handle, requestId, kind, items, nextPageHandle, error);
        }
        catch (OperationCanceledException)
        {
            if (_extrasRequests.TryGetValue(kind, out var current) && ReferenceEquals(current, cancellation))
                await SendExtrasResultAsync(handle, requestId, kind, items, null, new { message = "读取超时，请重试。" });
        }
        finally
        {
            if (_extrasRequests.TryGetValue(kind, out var current) && ReferenceEquals(current, cancellation)) _extrasRequests.Remove(kind);
        }
    }

    private Task SendExtrasResultAsync(string handle, long requestId, string kind, object items, string? nextPageHandle, object? error) =>
        ExecuteScriptAsync($"window.Auralis?.setPlatformExtras({JsonSerializer.Serialize(new { handle, requestId, kind, items, nextPageHandle, error }, WebJsonOptions)})");

    private static string? JsonText(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task HandleSavedPlaylistsAsync(JsonElement root)
    {
        var action = JsonText(root, "action");
        long? requestId = root.TryGetProperty("requestId", out var id) && id.TryGetInt64(out var value) ? value : null;
        try
        {
            if (action == "createSavedPlaylist") await _savedPlaylistStore.ChangeAsync("create", name: JsonText(root, "name"));
            if (action == "removeFromSavedPlaylist") await _savedPlaylistStore.ChangeAsync("remove", JsonText(root, "playlistId"), entryId: JsonText(root, "entryId"));
            if (action == "addToSavedPlaylist")
            {
                await RestoreSavedHandleAsync(JsonText(root, "handle"));
                SavedPlaylistEntry entry;
                if (JsonText(root, "handle") is { } handle && OnlinePlatforms.GetBackendTrack(handle) is { } track)
                    entry = new(Guid.NewGuid().ToString("N"), null, track.Id.ProviderId, track.Id.Value,
                        track.Title, string.Join("、", track.Artists.Select(a => a.Name)), track.Album?.Title ?? "在线单曲", track.Duration?.TotalSeconds ?? 0, track.MusicVideo?.Id.Value);
                else
                {
                    var localId = JsonText(root, "localId");
                    var local = _library.FirstOrDefault(p => CreateId(p.Key) == localId).Value;
                    if (local is null) throw new InvalidOperationException("歌曲入口已过期，请重新打开歌曲列表。");
                    var info = CreateTrackInfo(local);
                    entry = new(Guid.NewGuid().ToString("N"), localId, null, null, info.Title, info.Artist, info.Album, info.DurationSeconds ?? 0, null);
                }
                await _savedPlaylistStore.ChangeAsync("add", JsonText(root, "playlistId"), entry: entry);
            }
            var lists = await _savedPlaylistStore.LoadAsync();
            // Index local paths once: a mixed library must not scan/hash the entire local
            // library for every saved entry (up to 100 x 1000 references).
            var localById = _library.ToDictionary(pair => CreateId(pair.Key), pair => pair.Value);
            var items = lists.Select(p => new
            {
                p.Id, p.Name,
                entries = p.Entries.Select(e => new
                {
                    entryId = e.Id,
                    track = e.LocalId is null ? (object)OnlinePlatforms.RestoreSavedTrack(e)
                        : localById.TryGetValue(e.LocalId, out var stored)
                            ? CreateTrackInfo(stored) : new { id = e.LocalId, kind = "local", e.Title, e.Artist, e.Album, e.DurationSeconds, isPlayable = false }
                }).ToArray()
            }).ToArray();
            await ExecuteScriptAsync($"window.Auralis?.setSavedPlaylists({JsonSerializer.Serialize(new { items, action, requestId }, WebJsonOptions)})");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            await ExecuteScriptAsync($"window.Auralis?.setSavedPlaylists({JsonSerializer.Serialize(new { action, requestId, error = new { message = "无法更新歌单。请检查歌曲入口和名称；原歌单文件已保留。" } }, WebJsonOptions)})");
        }
    }
}
