using System.Security.Cryptography;
using Auralis.Services;

namespace Auralis.Playback.Host;

public enum PlaybackPackageIssue
{
    None, RootUnavailable, UnsafePath, TooManyPackages, ManifestMissing, ManifestInvalid,
    DuplicateId, ApiMismatch, HostTooOld, RuntimeMismatch, MissingCapability,
    PayloadMissing, PayloadUnexpected, PayloadChanged, PayloadTooLarge, ManifestChanged, IoFailure
}

/// <summary>Backend-only inspection snapshot. It does not grant trust or activation permission.</summary>
public sealed class PlaybackPackageSnapshot
{
    public string DirectoryPath { get; }
    public string ManifestSha256 { get; }
    public PlaybackComponentManifest Manifest { get; }
    internal PlaybackPackageSnapshot(string directory, string digest, PlaybackComponentManifest manifest)
        => (DirectoryPath, ManifestSha256, Manifest) = (directory, digest, manifest);
}
public sealed record PlaybackPackageFinding(PlaybackPackageIssue Issue, PlaybackPackageSnapshot? Package);

/// <summary>Only reads an explicitly supplied local directory. No assembly reflection/load, HTTP,
/// credential services, approval writes or automatic user-directory discovery.</summary>
public static class PlaybackComponentCatalog
{
    public const int MaximumPackages = 64;

    public static IReadOnlyList<PlaybackPackageFinding> Discover(string root, PlaybackCapabilities required,
        string runtimeIdentifier = "win-x64", CancellationToken cancellationToken = default)
    {
        ValidateRequirements(required, runtimeIdentifier);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var directory = Path.GetFullPath(root);
            if (directory.StartsWith("\\\\", StringComparison.Ordinal) || directory.StartsWith("//", StringComparison.Ordinal))
                return [new(PlaybackPackageIssue.UnsafePath, null)];
            if (!Directory.Exists(directory)) return [new(PlaybackPackageIssue.RootUnavailable, null)];
            if (!SafeLocalPath(directory)) return [new(PlaybackPackageIssue.UnsafePath, null)];
            var results = new List<PlaybackPackageFinding>();
            if (File.Exists(Path.Combine(directory, PlaybackComponentManifest.FileName)))
                results.Add(Inspect(directory, required, runtimeIdentifier, cancellationToken));
            else
            {
                // Bound enumeration before reading any package. A partial set must not win ID resolution.
                var children = Directory.EnumerateDirectories(directory).Take(MaximumPackages + 1).ToArray();
                if (children.Length > MaximumPackages) return [new(PlaybackPackageIssue.TooManyPackages, null)];
                foreach (var child in children.Order(StringComparer.OrdinalIgnoreCase))
                    results.Add(Inspect(child, required, runtimeIdentifier, cancellationToken));
            }
            var duplicates = results.Where(r => r.Package is not null)
                .GroupBy(r => r.Package!.Manifest.Descriptor.Id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            return Array.AsReadOnly(results.Select(r => r.Package is not null && duplicates.Contains(r.Package.Manifest.Descriptor.Id)
                ? r with { Issue = PlaybackPackageIssue.DuplicateId } : r).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (FileError(e)) { return [new(PlaybackPackageIssue.IoFailure, null)]; }
    }

    private static PlaybackPackageFinding Inspect(string directory, PlaybackCapabilities required, string runtime, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!SafeLocalPath(directory)) return new(PlaybackPackageIssue.UnsafePath, null);
            var file = Path.Combine(directory, PlaybackComponentManifest.FileName);
            if (!File.Exists(file)) return new(PlaybackPackageIssue.ManifestMissing, null);
            if (!SafeLocalPath(file)) return new(PlaybackPackageIssue.UnsafePath, null);
            var bytes = ReadManifest(file);
            var manifest = PlaybackComponentManifest.Parse(bytes);
            var snapshot = new PlaybackPackageSnapshot(directory, Convert.ToHexString(SHA256.HashData(bytes)), manifest);
            var metadata = manifest.Descriptor;
            var issue = metadata.ContractApiVersion != PlaybackComponentRegistry.ContractApiVersion ? PlaybackPackageIssue.ApiMismatch :
                metadata.MinimumHostVersion > PlaybackComponentRegistry.HostVersion ? PlaybackPackageIssue.HostTooOld :
                manifest.RuntimeIdentifier != runtime ? PlaybackPackageIssue.RuntimeMismatch :
                (metadata.Capabilities & required) != required ? PlaybackPackageIssue.MissingCapability : PlaybackPackageIssue.None;
            return new(issue, snapshot);
        }
        catch (FormatException) { return new(PlaybackPackageIssue.ManifestInvalid, null); }
        catch (Exception e) when (FileError(e)) { return new(PlaybackPackageIssue.IoFailure, null); }
    }

    /// <summary>Explicit full payload check, distinct from inert metadata discovery. Success is not
    /// publisher authentication, user approval or a race-free loading guarantee. Future activation must
    /// use approved immutable revisions and revalidate them; this method never loads code.</summary>
    public static async Task<PlaybackPackageIssue> VerifyPayloadAsync(PlaybackPackageSnapshot package, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        token.ThrowIfCancellationRequested();
        try
        {
            var root = package.DirectoryPath;
            if (!SafeLocalPath(root)) return PlaybackPackageIssue.UnsafePath;
            var manifestPath = Path.Combine(root, PlaybackComponentManifest.FileName);
            if (!SafeLocalPath(manifestPath)) return PlaybackPackageIssue.UnsafePath;
            if (Convert.ToHexString(SHA256.HashData(ReadManifest(manifestPath))) != package.ManifestSha256)
                return PlaybackPackageIssue.ManifestChanged;
            var declared = package.Manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(root);
            var directories = 0;
            long bytes = 0;
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                if (++directories > PlaybackComponentManifest.MaximumFiles) return PlaybackPackageIssue.PayloadTooLarge;
                if (!SafeLocalPath(directory)) return PlaybackPackageIssue.UnsafePath;
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (!SafeLocalPath(path)) return PlaybackPackageIssue.UnsafePath;
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (!PlaybackComponentManifest.ValidRelativePath(relative)) return PlaybackPackageIssue.UnsafePath;
                    if (Directory.Exists(path))
                    {
                        if (!declared.Keys.Any(f => f.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
                            return PlaybackPackageIssue.PayloadUnexpected;
                        if (pending.Count >= PlaybackComponentManifest.MaximumFiles) return PlaybackPackageIssue.PayloadTooLarge;
                        pending.Push(path); continue;
                    }
                    if (relative == PlaybackComponentManifest.FileName) continue;
                    if (!declared.TryGetValue(relative, out var expected) || !seen.Add(relative)) return PlaybackPackageIssue.PayloadUnexpected;
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (stream.Length != expected.Length) return PlaybackPackageIssue.PayloadChanged;
                    if ((bytes += stream.Length) > PlaybackComponentManifest.MaximumPayloadBytes) return PlaybackPackageIssue.PayloadTooLarge;
                    // Bound actual reads too, even on filesystems where a concurrent writer bypasses sharing.
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    long count = 0; int read;
                    while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        if ((count += read) > expected.Length) return PlaybackPackageIssue.PayloadChanged;
                        hash.AppendData(buffer, 0, read);
                    }
                    if (count != expected.Length || Convert.ToHexString(hash.GetHashAndReset()) != expected.Sha256)
                        return PlaybackPackageIssue.PayloadChanged;
                }
            }
            if (seen.Count != declared.Count) return PlaybackPackageIssue.PayloadMissing;
            // Metadata changed while hashing must not validate the original inspection snapshot.
            if (!SafeLocalPath(manifestPath)) return PlaybackPackageIssue.UnsafePath;
            return Convert.ToHexString(SHA256.HashData(ReadManifest(manifestPath))) == package.ManifestSha256
                ? PlaybackPackageIssue.None : PlaybackPackageIssue.ManifestChanged;
        }
        catch (OperationCanceledException) { throw; }
        catch (FormatException) { return PlaybackPackageIssue.ManifestChanged; }
        catch (Exception e) when (FileError(e)) { return PlaybackPackageIssue.IoFailure; }
    }

    private static byte[] ReadManifest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > PlaybackComponentManifest.MaximumManifestBytes) throw new FormatException();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new FormatException();
        return bytes;
    }
    private static bool SafeLocalPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal)) return false;
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    private static void ValidateRequirements(PlaybackCapabilities required, string runtime)
    {
        if (required == PlaybackCapabilities.None || (required & ~PlaybackCapabilities.CompletePlayer) != 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
    }
    private static bool FileError(Exception e) => e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
