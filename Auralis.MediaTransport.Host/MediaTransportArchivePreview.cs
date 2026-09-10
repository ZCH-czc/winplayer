using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Auralis.MediaTransport.Host;

public enum MediaTransportArchiveIssue { InvalidArchive, UnsafeEntry, LimitExceeded, ManifestMismatch, StorageFailure }
public sealed class MediaTransportArchiveException(MediaTransportArchiveIssue issue)
    : InvalidOperationException("MediaTransport archive operation failed.")
{
    public MediaTransportArchiveIssue Issue { get; } = issue;
}

/// <summary>Caller-owned confirmation lifetime. Cancel/close must DisposeAsync. Archive bytes are
/// copied and verified before approval; changing the original ZIP cannot replace the preview payload.</summary>
public sealed class MediaTransportArchivePreview : IAsyncDisposable
{
    private readonly object _owner;
    private readonly MediaTransportImportPreview _preview;
    private readonly MediaTransportArchiveStaging _staging;
    private readonly Action _release;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public MediaTransportDescriptor Descriptor => _preview.Descriptor;
    public string ManifestSha256 => _preview.ManifestSha256;
    public string ArchiveSha256 => _staging.ArchiveSha256;
    public int FileCount => _preview.FileCount;
    public long PayloadBytes => _preview.PayloadBytes;
    internal MediaTransportArchivePreview(object owner, MediaTransportImportPreview preview, MediaTransportArchiveStaging staging, Action release)
        => (_owner, _preview, _staging, _release) = (owner, preview, staging, release);

    internal async Task<MediaTransportInstalledRevision> ImportAsync(object owner,
        Func<MediaTransportImportPreview, Task<MediaTransportInstalledRevision>> import, CancellationToken token)
    {
        if (!ReferenceEquals(owner, _owner)) throw new MediaTransportInstallationException(MediaTransportInstallationIssue.ApprovalRequired);
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
internal sealed class MediaTransportArchiveStaging : IDisposable
{
    public const long MaximumArchiveBytes = 80L * 1024 * 1024;
    private const int MaximumEntries = MediaTransportComponentManifest.MaximumFiles * 2 + 1;
    private const int MaximumCentralDirectoryBytes = 8 * 1024 * 1024;
    private readonly string _parent;
    private readonly List<string> _files = [];
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    public string DirectoryPath { get; }
    public string ArchiveSha256 { get; }
    private MediaTransportArchiveStaging(string parent, string digest)
    {
        _parent = Path.GetFullPath(parent);
        DirectoryPath = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        ArchiveSha256 = digest;
    }

    internal static async Task<MediaTransportArchiveStaging> ExtractAsync(string archivePath, string parent, CancellationToken token)
    {
        MediaTransportArchiveStaging? staging = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!MediaTransportInstallationStore.SafePath(archivePath)) throw Error(MediaTransportArchiveIssue.UnsafeEntry);
            await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (input.Length is < 22 or > MaximumArchiveBytes) throw Error(MediaTransportArchiveIssue.LimitExceeded);
            ValidateDirectoryEnvelope(input); // Bound central metadata BEFORE ZipArchive allocates Entries.
            input.Position = 0;
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            input.Position = 0;
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count is 0 or > MaximumEntries) throw Error(MediaTransportArchiveIssue.LimitExceeded);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            var explicitDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                var path = directory ? entry.FullName[..^1] : entry.FullName;
                if (!MediaTransportComponentManifest.ValidRelativePath(path)) throw Error(MediaTransportArchiveIssue.UnsafeEntry);
                var mode = (entry.ExternalAttributes >> 16) & 0xf000;
                if ((mode != 0 && mode != (directory ? 0x4000 : 0x8000)) ||
                    (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                    (!directory && (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0))
                    throw Error(MediaTransportArchiveIssue.UnsafeEntry);
                if (directory)
                {
                    if (entry.Length != 0 || !explicitDirectories.Add(path) || explicitDirectories.Count > MediaTransportComponentManifest.MaximumFiles)
                        throw Error(MediaTransportArchiveIssue.UnsafeEntry);
                }
                else
                {
                    if (!entries.TryAdd(path, entry)) throw Error(MediaTransportArchiveIssue.UnsafeEntry);
                    if (entry.Length > MediaTransportComponentManifest.MaximumPayloadBytes ||
                        (total += entry.Length) > MediaTransportComponentManifest.MaximumPayloadBytes + MediaTransportComponentManifest.MaximumManifestBytes ||
                        entry.Length > Math.Max(1, entry.CompressedLength) * 1000L)
                        throw Error(MediaTransportArchiveIssue.LimitExceeded);
                }
            }
            if (!entries.TryGetValue(MediaTransportComponentManifest.FileName, out var manifestEntry) ||
                manifestEntry.FullName != MediaTransportComponentManifest.FileName || manifestEntry.Length is 0 or > MediaTransportComponentManifest.MaximumManifestBytes)
                throw Error(MediaTransportArchiveIssue.ManifestMismatch);
            var bytes = new byte[(int)manifestEntry.Length];
            await using (var stream = manifestEntry.Open())
            {
                await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                if (stream.ReadByte() != -1) throw Error(MediaTransportArchiveIssue.LimitExceeded);
            }
            var manifest = MediaTransportComponentManifest.Parse(bytes);
            if (entries.Count != manifest.Files.Count + 1) throw Error(MediaTransportArchiveIssue.ManifestMismatch);
            foreach (var file in manifest.Files)
                if (!entries.TryGetValue(file.Path, out var entry) || entry.FullName != file.Path || entry.Length != file.Length)
                    throw Error(MediaTransportArchiveIssue.ManifestMismatch);
            var allowedDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in entries.Keys)
            {
                var slash = path.LastIndexOf('/');
                while (slash >= 0) { allowedDirectories.Add(path[..slash]); slash = path.LastIndexOf('/', slash - 1); }
            }
            if (explicitDirectories.Any(d => !allowedDirectories.Contains(d))) throw Error(MediaTransportArchiveIssue.ManifestMismatch);
            if (allowedDirectories.Count >= MediaTransportComponentManifest.MaximumFiles) throw Error(MediaTransportArchiveIssue.LimitExceeded);
            // Reject case aliases of directory prefixes even when neither has an explicit ZIP entry.
            if (allowedDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allowedDirectories.Count ||
                allowedDirectories.Any(entries.ContainsKey)) throw Error(MediaTransportArchiveIssue.UnsafeEntry);

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
            if (error is OperationCanceledException or MediaTransportArchiveException) throw;
            if (error is InvalidDataException or FormatException or EndOfStreamException or NotSupportedException or ArgumentException)
                throw Error(MediaTransportArchiveIssue.InvalidArchive);
            if (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                throw Error(MediaTransportArchiveIssue.StorageFailure);
            throw;
        }
    }

    private async Task WriteEntryAsync(ZipArchiveEntry entry, long length, string digest, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = Path.Combine(DirectoryPath, entry.FullName);
        CreateDirectories(Path.GetDirectoryName(target)!);
        if (!MediaTransportInstallationStore.SafePath(target)) throw Error(MediaTransportArchiveIssue.UnsafeEntry);
        await using var input = entry.Open();
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        _files.Add(target);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long count = 0; int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if ((count += read) > length) throw Error(MediaTransportArchiveIssue.LimitExceeded);
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (count != length || Convert.ToHexString(hash.GetHashAndReset()) != digest)
            throw Error(MediaTransportArchiveIssue.ManifestMismatch);
    }
    private void CreateDirectories(string directory)
    {
        MediaTransportInstallationStore.EnsureDirectory(directory);
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
                throw Error(MediaTransportArchiveIssue.UnsafeEntry);
            foreach (var path in _files.Concat(_directories))
                if (!path.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && path != DirectoryPath ||
                    !MediaTransportInstallationStore.SafePath(path)) throw Error(MediaTransportArchiveIssue.UnsafeEntry);
            foreach (var path in _files) File.Delete(path);
            foreach (var path in _directories.OrderByDescending(p => p.Length))
                if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
            _files.Clear(); _directories.Clear();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw Error(MediaTransportArchiveIssue.StorageFailure); }
    }

    private static void ValidateDirectoryEnvelope(FileStream stream)
    {
        // Ordinary single-disk ZIP only. With <=513 entries and <=80 MiB, ZIP64 is unnecessary.
        var tail = new byte[(int)Math.Min(stream.Length, 65535 + 22)];
        stream.Position = stream.Length - tail.Length; stream.ReadExactly(tail);
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
        // No archive comment/trailer: avoids competing EOCD signatures in comments being interpreted
        // differently by the envelope checker and ZipArchive. Per-entry comments remain bounded.
        if (end < 0 || U16(tail, end + 20) != 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10))
            throw Error(MediaTransportArchiveIssue.InvalidArchive);
        var count = U16(tail, end + 10); var size = U32(tail, end + 12); var start = U32(tail, end + 16);
        if (count is 0 or > MaximumEntries || size > MaximumCentralDirectoryBytes) throw Error(MediaTransportArchiveIssue.LimitExceeded);
        if ((long)start + size != stream.Length - tail.Length + end) throw Error(MediaTransportArchiveIssue.InvalidArchive);
        stream.Position = start;
        var central = new byte[(int)size]; stream.ReadExactly(central);
        var offset = 0;
        for (var n = 0; n < count; n++)
        {
            if (offset > central.Length - 46 || U32(central, offset) != 0x02014b50) throw Error(MediaTransportArchiveIssue.InvalidArchive);
            var flags = U16(central, offset + 8); var method = U16(central, offset + 10);
            if ((flags & ~0x080e) != 0 || method is not (0 or 8) || U16(central, offset + 34) != 0 || U32(central, offset + 42) >= start)
                throw Error(MediaTransportArchiveIssue.InvalidArchive);
            if (U32(central, offset + 20) > MaximumArchiveBytes || U32(central, offset + 24) > MediaTransportComponentManifest.MaximumPayloadBytes)
                throw Error(MediaTransportArchiveIssue.LimitExceeded);
            offset += 46 + U16(central, offset + 28) + U16(central, offset + 30) + U16(central, offset + 32);
        }
        if (offset != central.Length) throw Error(MediaTransportArchiveIssue.InvalidArchive);
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static MediaTransportArchiveException Error(MediaTransportArchiveIssue issue) => new(issue);
}
