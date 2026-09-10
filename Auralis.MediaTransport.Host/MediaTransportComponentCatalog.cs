using System.Security.Cryptography;

namespace Auralis.MediaTransport.Host;

public enum MediaTransportPackageIssue
{
    None, RootUnavailable, UnsafePath, TooManyPackages, ManifestMissing, ManifestInvalid,
    DuplicateId, ApiMismatch, HostTooOld, RuntimeMismatch, MissingCapability,
    PayloadMissing, PayloadUnexpected, PayloadChanged, PayloadTooLarge, ManifestChanged, IoFailure
}

/// <summary>Backend-only inspection snapshot. It does not grant trust or activation permission.</summary>
public sealed class MediaTransportPackageSnapshot
{
    public string DirectoryPath { get; }
    public string ManifestSha256 { get; }
    public MediaTransportComponentManifest Manifest { get; }
    internal MediaTransportPackageSnapshot(string directory, string digest, MediaTransportComponentManifest manifest)
        => (DirectoryPath, ManifestSha256, Manifest) = (directory, digest, manifest);
}
public sealed record MediaTransportPackageFinding(MediaTransportPackageIssue Issue, MediaTransportPackageSnapshot? Package);

/// <summary>Only reads an explicitly supplied local directory. No assembly reflection/load, HTTP,
/// credential services, approval writes or automatic user-directory discovery.</summary>
public static class MediaTransportComponentCatalog
{
    public const int MaximumPackages = 64;

    public static IReadOnlyList<MediaTransportPackageFinding> Discover(string root, MediaTransportCapabilities required,
        string runtimeIdentifier = "win-x64", CancellationToken cancellationToken = default)
    {
        ValidateRequirements(required, runtimeIdentifier);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!Path.IsPathFullyQualified(root)) return [new(MediaTransportPackageIssue.UnsafePath, null)];
            var directory = Path.GetFullPath(root);
            if (directory.StartsWith("\\\\", StringComparison.Ordinal) || directory.StartsWith("//", StringComparison.Ordinal))
                return [new(MediaTransportPackageIssue.UnsafePath, null)];
            // Reject mapped network roots before Directory.Exists can contact their server.
            if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(directory)!).DriveType == DriveType.Network)
                return [new(MediaTransportPackageIssue.UnsafePath, null)];
            if (!Directory.Exists(directory)) return [new(MediaTransportPackageIssue.RootUnavailable, null)];
            if (!SafeLocalPath(directory)) return [new(MediaTransportPackageIssue.UnsafePath, null)];
            var results = new List<MediaTransportPackageFinding>();
            if (File.Exists(Path.Combine(directory, MediaTransportComponentManifest.FileName)))
                results.Add(Inspect(directory, required, runtimeIdentifier, cancellationToken));
            else
            {
                // Bound enumeration before reading any package. A partial set must not win ID resolution.
                var children = Directory.EnumerateDirectories(directory).Take(MaximumPackages + 1).ToArray();
                if (children.Length > MaximumPackages) return [new(MediaTransportPackageIssue.TooManyPackages, null)];
                foreach (var child in children.Order(StringComparer.OrdinalIgnoreCase))
                    results.Add(Inspect(child, required, runtimeIdentifier, cancellationToken));
            }
            var duplicates = results.Where(r => r.Package is not null)
                .GroupBy(r => r.Package!.Manifest.Descriptor.Id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            return Array.AsReadOnly(results.Select(r => r.Package is not null && duplicates.Contains(r.Package.Manifest.Descriptor.Id)
                ? r with { Issue = MediaTransportPackageIssue.DuplicateId } : r).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (FileError(e)) { return [new(MediaTransportPackageIssue.IoFailure, null)]; }
    }

    private static MediaTransportPackageFinding Inspect(string directory, MediaTransportCapabilities required, string runtime, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!SafeLocalPath(directory)) return new(MediaTransportPackageIssue.UnsafePath, null);
            var file = Path.Combine(directory, MediaTransportComponentManifest.FileName);
            if (!File.Exists(file)) return new(MediaTransportPackageIssue.ManifestMissing, null);
            if (!SafeLocalPath(file)) return new(MediaTransportPackageIssue.UnsafePath, null);
            var bytes = ReadManifest(file);
            var manifest = MediaTransportComponentManifest.Parse(bytes);
            var snapshot = new MediaTransportPackageSnapshot(directory, Convert.ToHexString(SHA256.HashData(bytes)), manifest);
            var metadata = manifest.Descriptor;
            var issue = metadata.ApiVersion != MediaTransportRegistry.ApiVersion ? MediaTransportPackageIssue.ApiMismatch :
                metadata.MinimumHostVersion > MediaTransportRegistry.HostVersion ? MediaTransportPackageIssue.HostTooOld :
                manifest.RuntimeIdentifier != runtime ? MediaTransportPackageIssue.RuntimeMismatch :
                (metadata.Capabilities & required) != required ? MediaTransportPackageIssue.MissingCapability : MediaTransportPackageIssue.None;
            return new(issue, snapshot);
        }
        catch (FormatException) { return new(MediaTransportPackageIssue.ManifestInvalid, null); }
        catch (Exception e) when (FileError(e)) { return new(MediaTransportPackageIssue.IoFailure, null); }
    }

    /// <summary>Explicit full payload check, distinct from inert metadata discovery. Success is not
    /// publisher authentication, user approval or a race-free loading guarantee. Future activation must
    /// use approved immutable revisions and revalidate them; this method never loads code.</summary>
    public static async Task<MediaTransportPackageIssue> VerifyPayloadAsync(MediaTransportPackageSnapshot package, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        token.ThrowIfCancellationRequested();
        try
        {
            var root = package.DirectoryPath;
            if (!SafeLocalPath(root)) return MediaTransportPackageIssue.UnsafePath;
            var manifestPath = Path.Combine(root, MediaTransportComponentManifest.FileName);
            if (!SafeLocalPath(manifestPath)) return MediaTransportPackageIssue.UnsafePath;
            if (Convert.ToHexString(SHA256.HashData(ReadManifest(manifestPath))) != package.ManifestSha256)
                return MediaTransportPackageIssue.ManifestChanged;
            var declared = package.Manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(root);
            var directories = 0;
            long bytes = 0;
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                if (++directories > MediaTransportComponentManifest.MaximumFiles) return MediaTransportPackageIssue.PayloadTooLarge;
                if (!SafeLocalPath(directory)) return MediaTransportPackageIssue.UnsafePath;
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (!SafeLocalPath(path)) return MediaTransportPackageIssue.UnsafePath;
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (!MediaTransportComponentManifest.ValidRelativePath(relative)) return MediaTransportPackageIssue.UnsafePath;
                    if (Directory.Exists(path))
                    {
                        if (!declared.Keys.Any(f => f.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
                            return MediaTransportPackageIssue.PayloadUnexpected;
                        if (pending.Count >= MediaTransportComponentManifest.MaximumFiles) return MediaTransportPackageIssue.PayloadTooLarge;
                        pending.Push(path); continue;
                    }
                    if (relative == MediaTransportComponentManifest.FileName) continue;
                    if (!declared.TryGetValue(relative, out var expected) || !seen.Add(relative)) return MediaTransportPackageIssue.PayloadUnexpected;
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (stream.Length != expected.Length) return MediaTransportPackageIssue.PayloadChanged;
                    if ((bytes += stream.Length) > MediaTransportComponentManifest.MaximumPayloadBytes) return MediaTransportPackageIssue.PayloadTooLarge;
                    // Bound actual reads too, even on filesystems where a concurrent writer bypasses sharing.
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    long count = 0; int read;
                    while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        if ((count += read) > expected.Length) return MediaTransportPackageIssue.PayloadChanged;
                        hash.AppendData(buffer, 0, read);
                    }
                    if (count != expected.Length || Convert.ToHexString(hash.GetHashAndReset()) != expected.Sha256)
                        return MediaTransportPackageIssue.PayloadChanged;
                }
            }
            if (seen.Count != declared.Count) return MediaTransportPackageIssue.PayloadMissing;
            // Metadata changed while hashing must not validate the original inspection snapshot.
            if (!SafeLocalPath(manifestPath)) return MediaTransportPackageIssue.UnsafePath;
            return Convert.ToHexString(SHA256.HashData(ReadManifest(manifestPath))) == package.ManifestSha256
                ? MediaTransportPackageIssue.None : MediaTransportPackageIssue.ManifestChanged;
        }
        catch (OperationCanceledException) { throw; }
        catch (FormatException) { return MediaTransportPackageIssue.ManifestChanged; }
        catch (Exception e) when (FileError(e)) { return MediaTransportPackageIssue.IoFailure; }
    }

    private static byte[] ReadManifest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > MediaTransportComponentManifest.MaximumManifestBytes) throw new FormatException();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new FormatException();
        return bytes;
    }
    private static bool SafeLocalPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal)) return false;
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network) return false;
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    private static void ValidateRequirements(MediaTransportCapabilities required, string runtime)
    {
        if (required == MediaTransportCapabilities.None || (required & ~MediaTransportCapabilities.Full) != 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
    }
    private static bool FileError(Exception e) => e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
