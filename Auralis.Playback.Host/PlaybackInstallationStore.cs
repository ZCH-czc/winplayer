using System.Text.Json;
using Auralis.Services;

namespace Auralis.Playback.Host;

public enum PlaybackInstallationIssue { None, InvalidState, InvalidPackage, ApprovalRequired, UnknownComponent, Busy, StorageFailure }
public sealed class PlaybackInstallationException(PlaybackInstallationIssue issue)
    : InvalidOperationException("Playback installation operation failed.")
{
    public PlaybackInstallationIssue Issue { get; } = issue;
}

/// <summary>Preview is inert, tied to this store instance and to the exact inspected bytes.</summary>
public sealed class PlaybackImportPreview
{
    internal object Owner { get; }
    internal PlaybackPackageSnapshot Package { get; }
    public PlaybackComponentDescriptor Descriptor => Package.Manifest.Descriptor;
    public string ManifestSha256 => Package.ManifestSha256;
    public int FileCount => Package.Manifest.Files.Count;
    public long PayloadBytes => Package.Manifest.Files.Sum(f => f.Length);
    internal PlaybackImportPreview(object owner, PlaybackPackageSnapshot package) => (Owner, Package) = (owner, package);
}

public sealed record PlaybackInstalledRevision(string ComponentId, string ManifestSha256, bool Enabled);
public sealed record PlaybackInstallationState(PlaybackInstallationIssue Issue,
    IReadOnlyList<PlaybackInstalledRevision> Components, string? SelectedId);
public sealed record PlaybackStartupPlan(PlaybackInstallationIssue Issue,
    IReadOnlyList<PlaybackComponentRegistration> Registrations, string? SelectedId,
    IReadOnlyList<string> RejectedComponentIds);

/// <summary>Explicit-directory, restart-only install state. Never touches the application's profiles,
/// executes discovery candidates, or changes an existing session. Receipts are local user decisions,
/// not publisher signatures or protection against code already running as this Windows user.</summary>
public sealed class PlaybackInstallationStore
{
    private const string StateFile = "playback-installations.json";
    private const int MaximumStateBytes = 65536;
    private readonly string _root;
    private readonly PlaybackCapabilities _required;
    private readonly string _runtime;
    private readonly object _owner = new();
    private int _archivePreviewActive;

    public PlaybackInstallationStore(string root, PlaybackCapabilities required, string runtimeIdentifier = "win-x64")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (required == PlaybackCapabilities.None || (required & ~PlaybackCapabilities.CompletePlayer) != 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // A dedicated child directory is required, never a drive/share root.
        if (Path.GetDirectoryName(_root) is null || !SafePath(_root)) throw Fail(PlaybackInstallationIssue.StorageFailure);
        _required = required; _runtime = runtimeIdentifier;
    }

    public async Task<PlaybackImportPreview> PreviewAsync(string unpackedPackageDirectory, CancellationToken token = default)
    {
        var package = Inspect(unpackedPackageDirectory);
        if (await PlaybackComponentCatalog.VerifyPayloadAsync(package, token).ConfigureAwait(false) != PlaybackPackageIssue.None)
            throw Fail(PlaybackInstallationIssue.InvalidPackage);
        return new(_owner, package);
    }

    /// <summary>Extracts one bounded ZIP into this store's private preview area without approving or
    /// loading it. Dispose the returned preview when its confirmation surface closes.</summary>
    public async Task<PlaybackArchivePreview> PreviewArchiveAsync(string archivePath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _archivePreviewActive, 1, 0) != 0)
            throw Fail(PlaybackInstallationIssue.Busy);
        PlaybackArchiveStaging? staging = null;
        try
        {
            using var lease = AcquireWrite();
            // Do not extract new candidates into a store whose existing receipt is unreadable.
            _ = ReadStrict();
            staging = await PlaybackArchiveStaging.ExtractAsync(archivePath, Path.Combine(_root, "previews"), token).ConfigureAwait(false);
            var preview = await PreviewAsync(staging.DirectoryPath, token).ConfigureAwait(false);
            return new(_owner, preview, staging, () => Interlocked.Exchange(ref _archivePreviewActive, 0));
        }
        catch (Exception error)
        {
            try { staging?.Dispose(); }
            finally { Interlocked.Exchange(ref _archivePreviewActive, 0); }
            if (StorageError(error)) throw Fail(PlaybackInstallationIssue.StorageFailure);
            throw;
        }
    }

    public Task<PlaybackInstalledRevision> ImportApprovedAsync(PlaybackArchivePreview preview,
        PlaybackPackageApproval approval, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return preview.ImportAsync(_owner, p => ImportApprovedAsync(p, approval, token), token);
    }

    /// <summary>Caller must ask for explicit trust using the preview identity/digest before this call.
    /// New/replaced revisions are OFF. Identical re-imports preserve the existing enable/selection state.</summary>
    public async Task<PlaybackInstalledRevision> ImportApprovedAsync(PlaybackImportPreview preview,
        PlaybackPackageApproval approval, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(approval);
        if (!ReferenceEquals(preview.Owner, _owner) || approval.ComponentId != preview.Descriptor.Id ||
            !string.Equals(approval.ManifestSha256, preview.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw Fail(PlaybackInstallationIssue.ApprovalRequired);
        token.ThrowIfCancellationRequested();
        try
        {
            using var lease = AcquireWrite();
            var state = ReadStrict();
            var existing = state.Components.SingleOrDefault(c => c.ComponentId == preview.Descriptor.Id);
            if (existing is null && state.Components.Count >= PlaybackComponentCatalog.MaximumPackages)
                throw Fail(PlaybackInstallationIssue.InvalidState);
            if (await PlaybackComponentCatalog.VerifyPayloadAsync(preview.Package, token).ConfigureAwait(false) != PlaybackPackageIssue.None)
                throw Fail(PlaybackInstallationIssue.InvalidPackage);
            var destination = RevisionPath(preview.ManifestSha256);
            if (!SafePath(destination)) throw Fail(PlaybackInstallationIssue.StorageFailure);
            if (!Directory.Exists(destination))
            {
                // Random private staging is never discovered or executed. Failed/cancelled stages are
                // retained for explicit maintenance; no recursive deletion of caller-controlled paths.
                var stage = Path.Combine(_root, "staging", Guid.NewGuid().ToString("N"));
                EnsureDirectory(stage);
                var manifestSource = Path.Combine(preview.Package.DirectoryPath, PlaybackComponentManifest.FileName);
                await CopyBoundedAsync(manifestSource, Path.Combine(stage, PlaybackComponentManifest.FileName),
                    new FileInfo(manifestSource).Length, PlaybackComponentManifest.MaximumManifestBytes, token).ConfigureAwait(false);
                foreach (var file in preview.Package.Manifest.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var target = Path.Combine(stage, file.Path);
                    EnsureDirectory(Path.GetDirectoryName(target)!);
                    await CopyBoundedAsync(Path.Combine(preview.Package.DirectoryPath, file.Path), target,
                        file.Length, PlaybackComponentManifest.MaximumPayloadBytes, token).ConfigureAwait(false);
                }
                await VerifyExactAsync(stage, preview.Descriptor.Id, preview.ManifestSha256, token).ConfigureAwait(false);
                EnsureDirectory(Path.GetDirectoryName(destination)!);
                token.ThrowIfCancellationRequested();
                Directory.Move(stage, destination); // Never overwrite a revision, including one in use.
            }
            await VerifyExactAsync(destination, preview.Descriptor.Id, preview.ManifestSha256, token).ConfigureAwait(false);
            var result = existing?.ManifestSha256 == preview.ManifestSha256
                ? existing : new PlaybackInstalledRevision(preview.Descriptor.Id, preview.ManifestSha256, false);
            var rows = state.Components.Where(c => c.ComponentId != result.ComponentId).Append(result).ToArray();
            var selected = state.SelectedId == result.ComponentId && !result.Enabled ? null : state.SelectedId;
            WriteState(new(PlaybackInstallationIssue.None, Array.AsReadOnly(rows), selected), token);
            return result;
        }
        catch (Exception e) when (StorageError(e)) { throw Fail(PlaybackInstallationIssue.StorageFailure); }
    }

    public PlaybackInstallationState ReadState()
    {
        try { return ReadStrict(); }
        catch (PlaybackInstallationException e) { return new(e.Issue, Array.Empty<PlaybackInstalledRevision>(), null); }
        catch (Exception e) when (StorageError(e)) { return new(PlaybackInstallationIssue.StorageFailure, Array.Empty<PlaybackInstalledRevision>(), null); }
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var lease = AcquireWrite();
            var state = ReadStrict();
            var item = state.Components.SingleOrDefault(c => c.ComponentId == id) ?? throw Fail(PlaybackInstallationIssue.UnknownComponent);
            if (enabled) await VerifyExactAsync(RevisionPath(item.ManifestSha256), id, item.ManifestSha256, token).ConfigureAwait(false);
            WriteState(state with { Components = Array.AsReadOnly(state.Components.Select(c => c.ComponentId == id ? c with { Enabled = enabled } : c).ToArray()),
                SelectedId = !enabled && state.SelectedId == id ? null : state.SelectedId }, token);
        }
        catch (Exception e) when (StorageError(e)) { throw Fail(PlaybackInstallationIssue.StorageFailure); }
    }

    public async Task SelectAsync(string? id, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var lease = AcquireWrite();
            var state = ReadStrict();
            if (id is not null)
            {
                var item = state.Components.SingleOrDefault(c => c.ComponentId == id && c.Enabled)
                    ?? throw Fail(PlaybackInstallationIssue.UnknownComponent);
                await VerifyExactAsync(RevisionPath(item.ManifestSha256), id, item.ManifestSha256, token).ConfigureAwait(false);
            }
            WriteState(state with { SelectedId = id }, token);
        }
        catch (Exception e) when (StorageError(e)) { throw Fail(PlaybackInstallationIssue.StorageFailure); }
    }

    /// <summary>Capture once when composing a NEW host after restart. No factory is activated here.
    /// Caller owns bundled fallback composition and must resolve duplicate IDs explicitly.</summary>
    public PlaybackStartupPlan ReadStartupPlan()
    {
        var state = ReadState();
        if (state.Issue != PlaybackInstallationIssue.None)
            return new(state.Issue, Array.Empty<PlaybackComponentRegistration>(), null, Array.Empty<string>());
        var registrations = new List<PlaybackComponentRegistration>(); var rejected = new List<string>();
        foreach (var item in state.Components.Where(c => c.Enabled))
        {
            try
            {
                var package = Inspect(RevisionPath(item.ManifestSha256));
                if (package.ManifestSha256 != item.ManifestSha256 || package.Manifest.Descriptor.Id != item.ComponentId)
                    throw Fail(PlaybackInstallationIssue.InvalidPackage);
                registrations.Add(PlaybackPackageLoader.CreateRegistration(package, new(item.ComponentId, item.ManifestSha256), true));
            }
            catch (PlaybackInstallationException) { rejected.Add(item.ComponentId); }
        }
        return new(PlaybackInstallationIssue.None, registrations.AsReadOnly(),
            registrations.Any(r => r.Descriptor.Id == state.SelectedId) ? state.SelectedId : null, rejected.AsReadOnly());
    }

    private PlaybackPackageSnapshot Inspect(string directory)
    {
        var findings = PlaybackComponentCatalog.Discover(directory, _required, _runtime);
        if (findings.Count != 1 || findings[0].Issue != PlaybackPackageIssue.None || findings[0].Package is not { } package ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), Path.TrimEndingDirectorySeparator(package.DirectoryPath), StringComparison.OrdinalIgnoreCase))
            throw Fail(PlaybackInstallationIssue.InvalidPackage);
        return package;
    }
    private async Task VerifyExactAsync(string path, string id, string digest, CancellationToken token)
    {
        var package = Inspect(path);
        if (package.Manifest.Descriptor.Id != id || package.ManifestSha256 != digest ||
            await PlaybackComponentCatalog.VerifyPayloadAsync(package, token).ConfigureAwait(false) != PlaybackPackageIssue.None)
            throw Fail(PlaybackInstallationIssue.InvalidPackage);
    }
    private string RevisionPath(string digest) => Path.Combine(_root, "revisions", digest);
    private FileStream AcquireWrite()
    {
        EnsureDirectory(_root);
        var path = Path.Combine(_root, "install.lock");
        if (!SafePath(path)) throw Fail(PlaybackInstallationIssue.StorageFailure);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw Fail(PlaybackInstallationIssue.Busy); }
    }

    private PlaybackInstallationState ReadStrict()
    {
        var path = Path.Combine(_root, StateFile);
        if (!SafePath(path)) throw Fail(PlaybackInstallationIssue.StorageFailure);
        if (!File.Exists(path)) return new(PlaybackInstallationIssue.None, Array.Empty<PlaybackInstalledRevision>(), null);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is 0 or > MaximumStateBytes) throw Fail(PlaybackInstallationIssue.InvalidState);
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw Fail(PlaybackInstallationIssue.InvalidState);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 5 });
            var root = Fields(document.RootElement, "schemaVersion", "selectedId", "components");
            if (root["schemaVersion"].GetInt32() != 1) throw Fail(PlaybackInstallationIssue.InvalidState);
            var selected = root["selectedId"].GetString();
            var rows = new List<PlaybackInstalledRevision>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in root["components"].EnumerateArray())
            {
                var row = Fields(element, "id", "approvedManifestSha256", "enabled");
                var id = row["id"].GetString(); var digest = row["approvedManifestSha256"].GetString();
                if (rows.Count >= PlaybackComponentCatalog.MaximumPackages || id is null || id.Length is 0 or > 100 ||
                    !id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-') ||
                    !char.IsAsciiLetterOrDigit(id[0]) || !char.IsAsciiLetterOrDigit(id[^1]) || !ids.Add(id) ||
                    digest is null || digest.Length != 64 || !digest.All(char.IsAsciiHexDigit)) throw Fail(PlaybackInstallationIssue.InvalidState);
                rows.Add(new(id, digest.ToUpperInvariant(), row["enabled"].GetBoolean()));
            }
            if (selected is not null && !rows.Any(r => r.ComponentId == selected && r.Enabled)) throw Fail(PlaybackInstallationIssue.InvalidState);
            return new(PlaybackInstallationIssue.None, rows.AsReadOnly(), selected);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw Fail(PlaybackInstallationIssue.InvalidState); }
    }
    private static Dictionary<string, JsonElement> Fields(JsonElement element, params string[] names)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value)) throw Fail(PlaybackInstallationIssue.InvalidState);
        if (fields.Count != names.Length) throw Fail(PlaybackInstallationIssue.InvalidState);
        return fields;
    }
    private void WriteState(PlaybackInstallationState state, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, selectedId = state.SelectedId,
            components = state.Components.OrderBy(c => c.ComponentId, StringComparer.Ordinal).Select(c => new { id = c.ComponentId,
                approvedManifestSha256 = c.ManifestSha256, enabled = c.Enabled }) });
        if (data.Length > MaximumStateBytes) throw Fail(PlaybackInstallationIssue.InvalidState);
        var target = Path.Combine(_root, StateFile); var temp = Path.Combine(_root, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
        if (!SafePath(target) || !SafePath(temp)) throw Fail(PlaybackInstallationIssue.StorageFailure);
        token.ThrowIfCancellationRequested();
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(data); stream.Flush(flushToDisk: true); }
        token.ThrowIfCancellationRequested();
        File.Move(temp, target, overwrite: true); // Single commit point; readers see the old or new receipt.
    }
    private static async Task CopyBoundedAsync(string source, string target, long expected, long limit, CancellationToken token)
    {
        if (expected < 0 || expected > limit || !SafePath(source) || !SafePath(target)) throw Fail(PlaybackInstallationIssue.InvalidPackage);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length != expected) throw Fail(PlaybackInstallationIssue.InvalidPackage);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        var buffer = new byte[65536]; long count = 0; int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if ((count += read) > expected) throw Fail(PlaybackInstallationIssue.InvalidPackage);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (count != expected) throw Fail(PlaybackInstallationIssue.InvalidPackage);
        output.Flush(flushToDisk: true);
    }
    internal static void EnsureDirectory(string path)
    {
        if (!SafePath(path)) throw Fail(PlaybackInstallationIssue.StorageFailure);
        Directory.CreateDirectory(path);
        if (!SafePath(path)) throw Fail(PlaybackInstallationIssue.StorageFailure);
    }
    internal static bool SafePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal)) return false;
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return true;
    }
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;
    private static PlaybackInstallationException Fail(PlaybackInstallationIssue issue) => new(issue);
}
