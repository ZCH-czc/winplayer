using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Auralis.Services;

namespace Auralis.Playback.Host;

public enum PlaybackArchiveIssue { InvalidArchive, UnsafeEntry, LimitExceeded, ManifestMismatch, StorageFailure }
public sealed class PlaybackArchiveException(PlaybackArchiveIssue issue)
    : InvalidOperationException("Playback archive operation failed.")
{
    public PlaybackArchiveIssue Issue { get; } = issue;
}

/// <summary>Caller-owned confirmation lifetime. Cancel/close must DisposeAsync. Archive bytes are
/// copied and verified before approval; changing the original ZIP cannot replace the preview payload.</summary>
public sealed class PlaybackArchivePreview : IAsyncDisposable
{
    private readonly object _owner;
    private readonly PlaybackImportPreview _preview;
    private readonly PlaybackArchiveStaging _staging;
    private readonly Action _release;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public PlaybackComponentDescriptor Descriptor => _preview.Descriptor;
    public string ManifestSha256 => _preview.ManifestSha256;
    public string ArchiveSha256 => _staging.ArchiveSha256;
    public int FileCount => _preview.FileCount;
    public long PayloadBytes => _preview.PayloadBytes;
    internal PlaybackArchivePreview(object owner, PlaybackImportPreview preview, PlaybackArchiveStaging staging, Action release)
        => (_owner, _preview, _staging, _release) = (owner, preview, staging, release);

    internal async Task<PlaybackInstalledRevision> ImportAsync(object owner,
        Func<PlaybackImportPreview, Task<PlaybackInstalledRevision>> import, CancellationToken token)
    {
        if (!ReferenceEquals(owner, _owner)) throw new PlaybackInstallationException(PlaybackInstallationIssue.ApprovalRequired);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); return await import(_preview).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { _staging.Dispose(); }
            finally { _release(); }
        }
        finally { _gate.Release(); }
    }
}

/// <summary>ZIP is a transport for the existing manifest, not another execution/approval contract.
/// No ExtractToDirectory, assembly loading, HTTP, platform IDs or user-profile defaults.</summary>
internal sealed class PlaybackArchiveStaging : IDisposable
{
    public const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = PlaybackComponentManifest.MaximumFiles * 2 + 1;
    private const int MaximumCentralDirectoryBytes = 8 * 1024 * 1024;
    private readonly string _parent;
    private readonly List<string> _files = [];
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    public string DirectoryPath { get; }
    public string ArchiveSha256 { get; }
    private PlaybackArchiveStaging(string parent, string digest)
    {
        _parent = Path.GetFullPath(parent);
        DirectoryPath = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        ArchiveSha256 = digest;
    }

    internal static async Task<PlaybackArchiveStaging> ExtractAsync(string archivePath, string parent, CancellationToken token)
    {
        PlaybackArchiveStaging? staging = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!PlaybackInstallationStore.SafePath(archivePath)) throw Error(PlaybackArchiveIssue.UnsafeEntry);
            await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (input.Length is < 22 or > MaximumArchiveBytes) throw Error(PlaybackArchiveIssue.LimitExceeded);
            ValidateDirectoryEnvelope(input); // Bound central metadata BEFORE ZipArchive allocates Entries.
            input.Position = 0;
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            input.Position = 0;
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count is 0 or > MaximumEntries) throw Error(PlaybackArchiveIssue.LimitExceeded);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            var explicitDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                var path = directory ? entry.FullName[..^1] : entry.FullName;
                if (!PlaybackComponentManifest.ValidRelativePath(path)) throw Error(PlaybackArchiveIssue.UnsafeEntry);
                var mode = (entry.ExternalAttributes >> 16) & 0xf000;
                if ((mode != 0 && mode != (directory ? 0x4000 : 0x8000)) ||
                    (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                    (!directory && (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0))
                    throw Error(PlaybackArchiveIssue.UnsafeEntry);
                if (directory)
                {
                    if (entry.Length != 0 || !explicitDirectories.Add(path) || explicitDirectories.Count > PlaybackComponentManifest.MaximumFiles)
                        throw Error(PlaybackArchiveIssue.UnsafeEntry);
                }
                else
                {
                    if (!entries.TryAdd(path, entry)) throw Error(PlaybackArchiveIssue.UnsafeEntry);
                    if (entry.Length > PlaybackComponentManifest.MaximumPayloadBytes ||
                        (total += entry.Length) > PlaybackComponentManifest.MaximumPayloadBytes + PlaybackComponentManifest.MaximumManifestBytes ||
                        entry.Length > Math.Max(1, entry.CompressedLength) * 1000L)
                        throw Error(PlaybackArchiveIssue.LimitExceeded);
                }
            }
            if (!entries.TryGetValue(PlaybackComponentManifest.FileName, out var manifestEntry) ||
                manifestEntry.FullName != PlaybackComponentManifest.FileName || manifestEntry.Length is 0 or > PlaybackComponentManifest.MaximumManifestBytes)
                throw Error(PlaybackArchiveIssue.ManifestMismatch);
            var bytes = new byte[(int)manifestEntry.Length];
            await using (var stream = manifestEntry.Open())
            {
                await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                if (stream.ReadByte() != -1) throw Error(PlaybackArchiveIssue.LimitExceeded);
            }
            var manifest = PlaybackComponentManifest.Parse(bytes);
            if (entries.Count != manifest.Files.Count + 1) throw Error(PlaybackArchiveIssue.ManifestMismatch);
            foreach (var file in manifest.Files)
                if (!entries.TryGetValue(file.Path, out var entry) || entry.FullName != file.Path || entry.Length != file.Length)
                    throw Error(PlaybackArchiveIssue.ManifestMismatch);
            var allowedDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in entries.Keys)
            {
                var slash = path.LastIndexOf('/');
                while (slash >= 0) { allowedDirectories.Add(path[..slash]); slash = path.LastIndexOf('/', slash - 1); }
            }
            if (explicitDirectories.Any(d => !allowedDirectories.Contains(d))) throw Error(PlaybackArchiveIssue.ManifestMismatch);
            if (allowedDirectories.Count > PlaybackComponentManifest.MaximumFiles) throw Error(PlaybackArchiveIssue.LimitExceeded);
            // Reject case aliases of directory prefixes even when neither has an explicit ZIP entry.
            if (allowedDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allowedDirectories.Count ||
                allowedDirectories.Any(entries.ContainsKey)) throw Error(PlaybackArchiveIssue.UnsafeEntry);

            staging = new(parent, digest);
            staging.CreateDirectories(staging.DirectoryPath);
            await staging.WriteEntryAsync(manifestEntry, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), token).ConfigureAwait(false);
            foreach (var file in manifest.Files)
                await staging.WriteEntryAsync(entries[file.Path], file.Length, file.Sha256, token).ConfigureAwait(false);
            return staging;
        }
        catch (Exception error)
        {
            staging?.Dispose();
            if (error is OperationCanceledException or PlaybackArchiveException) throw;
            if (error is InvalidDataException or FormatException or EndOfStreamException or NotSupportedException or ArgumentException)
                throw Error(PlaybackArchiveIssue.InvalidArchive);
            if (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                throw Error(PlaybackArchiveIssue.StorageFailure);
            throw;
        }
    }

    private async Task WriteEntryAsync(ZipArchiveEntry entry, long length, string digest, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = Path.Combine(DirectoryPath, entry.FullName);
        CreateDirectories(Path.GetDirectoryName(target)!);
        if (!PlaybackInstallationStore.SafePath(target)) throw Error(PlaybackArchiveIssue.UnsafeEntry);
        await using var input = entry.Open();
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        _files.Add(target);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long count = 0; int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if ((count += read) > length) throw Error(PlaybackArchiveIssue.LimitExceeded);
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (count != length || Convert.ToHexString(hash.GetHashAndReset()) != digest)
            throw Error(PlaybackArchiveIssue.ManifestMismatch);
    }
    private void CreateDirectories(string directory)
    {
        PlaybackInstallationStore.EnsureDirectory(directory);
        for (var path = directory; path != _parent && path is not null; path = Path.GetDirectoryName(path))
            _directories.Add(path);
    }
    public void Dispose()
    {
        // Only paths recorded as created by this extraction; no recursive deletion, no link traversal,
        // no deletion of unknown files introduced after extraction. Parent/store/revisions stay intact.
        try
        {
            if (Path.GetDirectoryName(DirectoryPath) != _parent || !Guid.TryParseExact(Path.GetFileName(DirectoryPath), "N", out _))
                throw Error(PlaybackArchiveIssue.UnsafeEntry);
            foreach (var path in _files.Concat(_directories))
                if (!path.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && path != DirectoryPath ||
                    !PlaybackInstallationStore.SafePath(path)) throw Error(PlaybackArchiveIssue.UnsafeEntry);
            foreach (var path in _files) File.Delete(path);
            foreach (var path in _directories.OrderByDescending(p => p.Length))
                if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
            _files.Clear(); _directories.Clear();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw Error(PlaybackArchiveIssue.StorageFailure); }
    }

    private static void ValidateDirectoryEnvelope(FileStream stream)
    {
        // Ordinary single-disk ZIP only. With <8194 entries and <512 MiB, ZIP64 is unnecessary.
        var tail = new byte[(int)Math.Min(stream.Length, 65535 + 22)];
        stream.Position = stream.Length - tail.Length; stream.ReadExactly(tail);
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
        // No archive comment/trailer: avoids competing EOCD signatures in comments being interpreted
        // differently by the envelope checker and ZipArchive. Per-entry comments remain bounded.
        if (end < 0 || U16(tail, end + 20) != 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10))
            throw Error(PlaybackArchiveIssue.InvalidArchive);
        var count = U16(tail, end + 10); var size = U32(tail, end + 12); var start = U32(tail, end + 16);
        if (count is 0 or > MaximumEntries || size > MaximumCentralDirectoryBytes) throw Error(PlaybackArchiveIssue.LimitExceeded);
        if ((long)start + size != stream.Length - tail.Length + end) throw Error(PlaybackArchiveIssue.InvalidArchive);
        stream.Position = start;
        var central = new byte[(int)size]; stream.ReadExactly(central);
        var offset = 0;
        for (var n = 0; n < count; n++)
        {
            if (offset > central.Length - 46 || U32(central, offset) != 0x02014b50) throw Error(PlaybackArchiveIssue.InvalidArchive);
            var flags = U16(central, offset + 8); var method = U16(central, offset + 10);
            if ((flags & ~0x080e) != 0 || method is not (0 or 8) || U16(central, offset + 34) != 0 || U32(central, offset + 42) >= start)
                throw Error(PlaybackArchiveIssue.InvalidArchive);
            if (U32(central, offset + 20) > MaximumArchiveBytes || U32(central, offset + 24) > PlaybackComponentManifest.MaximumPayloadBytes)
                throw Error(PlaybackArchiveIssue.LimitExceeded);
            offset += 46 + U16(central, offset + 28) + U16(central, offset + 30) + U16(central, offset + 32);
        }
        if (offset != central.Length) throw Error(PlaybackArchiveIssue.InvalidArchive);
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static PlaybackArchiveException Error(PlaybackArchiveIssue issue) => new(issue);
}
