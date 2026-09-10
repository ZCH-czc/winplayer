using System.IO;
using System.Text.Json;

namespace Auralis.Services;

// Deliberately separate from LibraryStore: persisted online references are never file paths or LAN tracks.
public sealed record SavedPlaylistEntry(string Id, string? LocalId, string? ProviderId, string? EntityId,
    string Title, string Artist, string Album, double DurationSeconds, string? VideoId);
public sealed record SavedPlaylist(string Id, string Name, List<SavedPlaylistEntry> Entries);

public sealed class SavedPlaylistStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public SavedPlaylistStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Auralis", "saved-playlists.json")) { }
    public SavedPlaylistStore(string path) => _path = path;

    private async Task<List<SavedPlaylist>> ReadAsync()
    {
        if (!File.Exists(_path)) return [];
        if (new FileInfo(_path).Length > 32 * 1024 * 1024) throw new IOException("歌单文件超过安全大小限制。");
        await using var file = File.OpenRead(_path);
        var lists = await JsonSerializer.DeserializeAsync<List<SavedPlaylist>>(file, Options) ?? [];
        if (lists.Count > 100 || lists.Any(p => p is null || string.IsNullOrEmpty(p.Id) || p.Name is null || p.Name.Length > 100 || p.Entries is null || p.Entries.Count > 1000 || p.Entries.Any(e => !IsValid(e))))
            throw new IOException("歌单文件格式无效；原文件已保留。");
        return lists;
    }

    public static bool IsValid(SavedPlaylistEntry? e) => e is not null && e.Id is { Length: > 0 and <= 64 } && e.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && e.Title is { Length: <= 512 } && e.Artist is { Length: <= 512 } && e.Album is { Length: <= 512 }
        && double.IsFinite(e.DurationSeconds) && e.DurationSeconds >= 0 && e.DurationSeconds <= 604800
        && (e.LocalId is { Length: > 0 and <= 128 } && e.ProviderId is null && e.EntityId is null && e.VideoId is null
            || e.LocalId is null && SafeProvider(e.ProviderId)
            && SafeEntity(e.EntityId) && (e.VideoId is null || SafeEntity(e.VideoId)));

    // Persistence validates identity syntax, not the currently installed provider inventory. Missing or
    // disabled plugins must not make a user's saved references unreadable; activation is a router concern.
    private static bool SafeProvider(string? id) => id is { Length: > 0 and <= 64 } &&
        char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    // Reject URLs, paths and signed URL material even if a provider violates the reference contract.
    private static bool SafeEntity(string? id) => id is { Length: > 0 and <= 1024 }
        && !id.Contains('/') && !id.Contains('\\') && !id.Contains('?') && !id.Contains('#') && !id.Contains('@') && !id.Contains('&') && !id.Any(char.IsControl);

    public async Task<IReadOnlyList<SavedPlaylist>> LoadAsync()
    {
        await _gate.WaitAsync();
        try { return await ReadAsync(); } finally { _gate.Release(); }
    }

    public async Task ChangeAsync(string action, string? playlistId = null, string? name = null, SavedPlaylistEntry? entry = null, string? entryId = null)
    {
        await _gate.WaitAsync();
        try
        {
            var lists = await ReadAsync();
            if (action == "create")
            {
                if (lists.Count >= 100 || string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new InvalidOperationException("歌单名称不能为空或超过 100 字，最多创建 100 个歌单。");
                lists.Add(new SavedPlaylist(Guid.NewGuid().ToString("N"), name.Trim(), []));
            }
            else
            {
                var list = lists.FirstOrDefault(p => p.Id == playlistId) ?? throw new InvalidOperationException("歌单不存在。");
                if (action == "add")
                {
                    if (!IsValid(entry)) throw new InvalidOperationException("无法保存这首歌曲的引用。");
                    if (!list.Entries.Any(e => e.LocalId == entry!.LocalId &&
                        string.Equals(e.ProviderId, entry.ProviderId, StringComparison.OrdinalIgnoreCase) && e.EntityId == entry.EntityId))
                    {
                        if (list.Entries.Count >= 1000) throw new InvalidOperationException("每个歌单最多保存 1000 首歌曲。");
                        list.Entries.Add(entry!);
                    }
                }
                else if (action == "remove") list.Entries.RemoveAll(e => e.Id == entryId);
                else throw new InvalidOperationException("无效的歌单操作。");
            }
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"saved-playlists.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, lists, Options);
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }
}
