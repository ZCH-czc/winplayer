using System.Text.Json;
using Auralis.Services;

namespace Auralis.Playback.Host;

public sealed record PlaybackComponentFile(string Path, long Length, string Sha256);

/// <summary>Backend-only immutable package declaration, not approval to execute it.</summary>
public sealed class PlaybackComponentManifest
{
    public const string FileName = "playback.component.json";
    public const int MaximumManifestBytes = 1024 * 1024;
    public const int MaximumFiles = 4096;
    public const long MaximumPayloadBytes = 512L * 1024 * 1024;
    public PlaybackComponentDescriptor Descriptor { get; }
    public string RuntimeIdentifier { get; }
    public string EntryAssembly { get; }
    public string EntryType { get; }
    public IReadOnlyList<PlaybackComponentFile> Files { get; }

    private PlaybackComponentManifest(PlaybackComponentDescriptor descriptor, string runtime, string assembly,
        string type, PlaybackComponentFile[] files)
        => (Descriptor, RuntimeIdentifier, EntryAssembly, EntryType, Files) =
            (descriptor, runtime, assembly, type, Array.AsReadOnly(files));

    // Strict, versioned schema: unknown and duplicate fields must not silently change permissions/meaning.
    public static PlaybackComponentManifest Parse(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length is 0 or > MaximumManifestBytes) throw new FormatException("Invalid playback manifest.");
        try
        {
            using var json = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 12 });
            var root = Object(json.RootElement, "schemaVersion", "kind", "id", "displayName", "version",
                "contractApiVersion", "minimumHostVersion", "runtimeIdentifier", "capabilities", "entryAssembly", "entryType", "files");
            if (root["schemaVersion"].GetInt32() != 1 || root["kind"].GetString() != "playback") throw Invalid();
            var id = String(root["id"], 100);
            if (!id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-') ||
                !char.IsAsciiLetterOrDigit(id[0]) || !char.IsAsciiLetterOrDigit(id[^1])) throw Invalid();
            var name = String(root["displayName"], 80);
            var version = VersionValue(root["version"]);
            var minimum = VersionValue(root["minimumHostVersion"]);
            var api = root["contractApiVersion"].GetInt32();
            if (api <= 0) throw Invalid();
            var runtime = String(root["runtimeIdentifier"], 40);
            if (!runtime.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) throw Invalid();
            var assembly = String(root["entryAssembly"], 240);
            if (!ValidRelativePath(assembly) || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw Invalid();
            var type = String(root["entryType"], 240);
            if (type.Split('.').Any(part => part.Length == 0 || !(char.IsAsciiLetter(part[0]) || part[0] == '_') ||
                part.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))) throw Invalid();
            var capabilities = PlaybackCapabilities.None;
            foreach (var value in root["capabilities"].EnumerateArray())
            {
                var flag = String(value, 32) switch
                {
                    "audio" => PlaybackCapabilities.Audio, "videoFrames" => PlaybackCapabilities.VideoFrames,
                    "separateAudio" => PlaybackCapabilities.SeparateAudio, "seek" => PlaybackCapabilities.Seek,
                    "rate" => PlaybackCapabilities.Rate, "outputDevices" => PlaybackCapabilities.OutputDevices,
                    "mute" => PlaybackCapabilities.Mute, _ => throw Invalid()
                };
                if ((capabilities & flag) != 0) throw Invalid();
                capabilities |= flag;
            }
            if (capabilities == PlaybackCapabilities.None) throw Invalid();
            var files = new List<PlaybackComponentFile>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var item in root["files"].EnumerateArray())
            {
                if (files.Count >= MaximumFiles) throw Invalid();
                var row = Object(item, "path", "length", "sha256");
                var path = String(row["path"], 240);
                var length = row["length"].GetInt64();
                var hash = String(row["sha256"], 64);
                if (!ValidRelativePath(path) || path.Equals(FileName, StringComparison.OrdinalIgnoreCase) ||
                    !paths.Add(path) || length < 0 || length > MaximumPayloadBytes ||
                    (total += length) > MaximumPayloadBytes || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) throw Invalid();
                files.Add(new(path, length, hash.ToUpperInvariant()));
            }
            if (files.Count == 0 || !files.Any(f => f.Path == assembly && f.Length > 0)) throw Invalid();
            // A file cannot also be a directory prefix of another file, even with different case.
            foreach (var path in paths)
            {
                var slash = path.IndexOf('/');
                while (slash >= 0)
                {
                    if (paths.Contains(path[..slash])) throw Invalid();
                    slash = path.IndexOf('/', slash + 1);
                }
            }
            return new(new(id, name, version, api, minimum, capabilities), runtime, assembly, type, files.ToArray());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or OverflowException or FormatException)
        { throw Invalid(); }
    }

    internal static bool ValidRelativePath(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 240) return false;
        return value.Split('/').All(part => part.Length is > 0 and <= 100 && part is not "." and not ".." &&
            part[0] != '.' && part[^1] != '.' && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') &&
            !Reserved.Contains(part.Split('.')[0]));
    }
    private static readonly HashSet<string> Reserved = new(
        new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }
            .Concat(Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })), StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, JsonElement> Object(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !result.TryAdd(property.Name, property.Value)) throw Invalid();
        if (result.Count != fields.Length) throw Invalid();
        return result;
    }
    private static string String(JsonElement value, int maximum)
    {
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text != text.Trim() || text.Any(char.IsControl)) throw Invalid();
        return text;
    }
    private static Version VersionValue(JsonElement value)
    {
        var text = String(value, 40);
        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || p.Any(c => !char.IsAsciiDigit(c))) ||
            !Version.TryParse(text, out var version)) throw Invalid();
        return version;
    }
    private static FormatException Invalid() => new("Invalid playback manifest.");
}
