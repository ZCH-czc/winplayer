using System.IO;
using System.Text.Json;
using Auralis.Models;

namespace Auralis.Services;

public sealed class LibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _libraryFile;

    public LibraryStore()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Auralis");
        Directory.CreateDirectory(appData);
        _libraryFile = Path.Combine(appData, "library.json");
    }

    public async Task<List<StoredTrack>> LoadAsync()
    {
        if (!File.Exists(_libraryFile))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_libraryFile);
            return await JsonSerializer.DeserializeAsync<List<StoredTrack>>(stream, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<StoredTrack> tracks)
    {
        var directory = Path.GetDirectoryName(_libraryFile)!;
        var temporaryFile = Path.Combine(directory, "library.tmp.json");
        await using (var stream = File.Create(temporaryFile))
        {
            await JsonSerializer.SerializeAsync(stream, tracks, JsonOptions);
        }

        File.Move(temporaryFile, _libraryFile, true);
    }
}
