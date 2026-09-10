using System.IO;
using System.Text.Json;

namespace Auralis.Services;

public sealed class MusicFolderStore
{
    private const int MaximumFolderCount = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _settingsFile;

    public MusicFolderStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Auralis",
            "music-folders.json"))
    {
    }

    public MusicFolderStore(string settingsFile)
    {
        _settingsFile = settingsFile;
    }

    public async Task<IReadOnlyList<string>> LoadAsync()
    {
        if (!File.Exists(_settingsFile))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_settingsFile);
            var folders = await JsonSerializer.DeserializeAsync<List<string>>(stream, JsonOptions) ?? [];
            return Normalize(folders);
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<string> folders)
    {
        var normalized = Normalize(folders);
        var directory = Path.GetDirectoryName(_settingsFile)
            ?? throw new InvalidOperationException("音乐文件夹设置文件必须位于有效目录中。");
        Directory.CreateDirectory(directory);
        var temporaryFile = Path.Combine(directory, $"music-folders.{Guid.NewGuid():N}.tmp.json");

        try
        {
            await using (var stream = File.Create(temporaryFile))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions);
            }

            File.Move(temporaryFile, _settingsFile, true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? folders)
    {
        if (folders is null)
        {
            return [];
        }

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!seen.Add(fullPath))
            {
                continue;
            }

            normalized.Add(fullPath);
            if (normalized.Count >= MaximumFolderCount)
            {
                break;
            }
        }

        return normalized;
    }

    public static IReadOnlyList<string> CollapseWatchRoots(IEnumerable<string> folders)
    {
        var normalized = Normalize(folders)
            .OrderBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var roots = new List<string>();

        foreach (var folder in normalized)
        {
            if (roots.Any(root => IsPathWithinRoot(folder, root)))
            {
                continue;
            }

            roots.Add(folder);
        }

        return roots;
    }

    public static bool IsPathWithinRoot(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root);
            var relative = Path.GetRelativePath(fullRoot, fullPath);
            return relative == "." ||
                   (!Path.IsPathRooted(relative) &&
                    !relative.Equals("..", StringComparison.Ordinal) &&
                    !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                    !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
