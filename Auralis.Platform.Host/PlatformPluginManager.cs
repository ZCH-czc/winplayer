using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Auralis.Platform.Host;

public sealed record ManagedPluginSetting(bool Enabled, string? Package = null);
public sealed record ManagedPluginState(int SchemaVersion, Dictionary<string, ManagedPluginSetting> Plugins);
public sealed record PluginSessionPlan(IReadOnlyList<string> Roots, IReadOnlySet<string> EnabledIds);
public sealed record PluginPackagePreview(string Token, string Id, string DisplayName, string Version,
    string Sha256, IReadOnlyList<string> Providers, IReadOnlyList<string> Capabilities,
    IReadOnlyList<PlatformCredentialAlias>? CredentialAliases = null, PlatformHostRequirements? HostRequirements = null);
public sealed record ManagedPluginItem(string Id, string DisplayName, string Version, IReadOnlyList<string> Providers,
    string State, bool Enabled, bool Active, bool CanEnable, string? CompatibilityIssue = null);
public sealed record ManagedPluginInventory(IReadOnlyList<ManagedPluginItem> Items, IReadOnlyList<string> Issues);
public sealed record PluginImportBatchItem(string FileName, PluginPackagePreview? Preview, string? Error);
public sealed record PluginImportBatchPreview(string Token, IReadOnlyList<PluginImportBatchItem> Items);
public sealed record PluginImportResult(string FileName, string? Id, string? Error);

/// <summary>
/// Explicit local import and opt-in state. Packages are immutable revisions; registry changes take effect
/// in a new host session. No assemblies, accounts, HTTP clients or platform-specific implementations here.
/// This is an integrity boundary for trusted in-process code, not a malicious-code sandbox.
/// </summary>
public sealed class PlatformPluginManager
{
    private const long Limit = 128L * 1024 * 1024;
    private static readonly Regex IdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex FilePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*\\.(dll|json)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly string _root;
    private readonly string[] _legacyRoots;
    private readonly PlatformHostCompatibility _compatibility;
    private readonly int _minimumManifestSchemaVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Prepared? _prepared;
    private readonly List<(string FileName, Prepared Package)> _batch = [];
    private PluginImportBatchPreview? _batchPreview;
    public const int MaximumBatchCount = 16;
    private const long BatchLimit = 256L * 1024 * 1024;
    private sealed record Prepared(string Directory, PlatformPluginManifest Manifest, PluginPackagePreview Preview,
        Dictionary<string, string> Hashes);

    public PlatformPluginManager(string storageRoot, IEnumerable<string> legacyRoots, PlatformHostCompatibility? compatibility = null,
        int minimumManifestSchemaVersion = PlatformPluginManifestSchema.MinimumRuntimeVersion)
    {
        _root = Path.GetFullPath(storageRoot);
        _legacyRoots = legacyRoots.Select(Path.GetFullPath).ToArray();
        _compatibility = compatibility ?? PlatformHostCompatibility.Current;
        if (minimumManifestSchemaVersion is < 1 or > PlatformPluginManifestSchema.CurrentVersion)
            throw new ArgumentOutOfRangeException(nameof(minimumManifestSchemaVersion));
        _minimumManifestSchemaVersion = minimumManifestSchemaVersion;
    }
    public string UserPluginFolder => _root;
    private string RegistryPath => Path.Combine(_root, "platform-state.json");
    private string PackagesRoot => Path.Combine(_root, "Packages");

    private static void ValidateId(string id)
    {
        if (!IdPattern.IsMatch(id) || id.Contains("..", StringComparison.Ordinal)) throw new InvalidDataException("Invalid plugin id.");
    }
    private static void NoLinks(string path)
    {
        // Reject links before creating/writing any managed path, including ancestor links.
        for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) &&
                (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked path rejected.");
    }
    private async Task<ManagedPluginState> ReadStateAsync(CancellationToken token)
    {
        NoLinks(RegistryPath);
        if (!File.Exists(RegistryPath)) return new(1, new(StringComparer.OrdinalIgnoreCase));
        if (new FileInfo(RegistryPath).Length > 65536) throw new InvalidDataException("State too large.");
        await using var input = File.OpenRead(RegistryPath);
        var state = await JsonSerializer.DeserializeAsync<ManagedPluginState>(input, cancellationToken: token).ConfigureAwait(false);
        if (state?.SchemaVersion != 1 || state.Plugins is null || state.Plugins.Count > 128) throw new InvalidDataException("Invalid state.");
        var map = new Dictionary<string, ManagedPluginSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, setting) in state.Plugins)
        {
            ValidateId(id);
            if (setting is null || (setting.Package is not null && !Guid.TryParseExact(setting.Package, "N", out _)) || !map.TryAdd(id, setting))
                throw new InvalidDataException("Invalid state entry.");
        }
        return new(1, map);
    }
    private async Task SaveStateAsync(ManagedPluginState state, CancellationToken token)
    {
        if (state.Plugins.Count > 128) throw new InvalidDataException("Too many plugins.");
        NoLinks(RegistryPath);
        Directory.CreateDirectory(_root);
        var temp = Path.Combine(_root, ".state-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state), token).ConfigureAwait(false);
            NoLinks(RegistryPath);
            File.Move(temp, RegistryPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private IReadOnlyList<string> RootsFor(ManagedPluginState state)
    {
        var roots = new List<string>();
        foreach (var legacy in _legacyRoots)
        {
            NoLinks(legacy);
            if (!Directory.Exists(legacy)) continue;
            // Legacy test bundles are only candidates, never enabled implicitly. A user-selected revision
            // of the same ID replaces the old candidate. Different IDs with duplicate providers still fail.
            if (File.Exists(Path.Combine(legacy, "platform.plugin.json"))) roots.Add(legacy);
            foreach (var child in Directory.EnumerateDirectories(legacy))
                if (!state.Plugins.TryGetValue(Path.GetFileName(child), out var setting) || setting.Package is null) roots.Add(child);
        }
        foreach (var (id, setting) in state.Plugins)
            if (setting.Package is not null)
            {
                var folder = Path.Combine(PackagesRoot, setting.Package, id);
                NoLinks(folder);
                roots.Add(folder);
            }
        return roots;
    }
    public async Task<PluginSessionPlan> CreateSessionPlanAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(token).ConfigureAwait(false);
            var enabled = state.Plugins.Where(p => p.Value.Enabled).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var roots = new List<string>();
            foreach (var root in RootsFor(state))
            {
                var candidate = await new PlatformPluginCatalog([root], _compatibility).DiscoverAsync(token).ConfigureAwait(false);
                if (candidate.Plugins.Any(p => enabled.Contains(p.Id) && p.SchemaVersion >= _minimumManifestSchemaVersion)) roots.Add(root);
            }
            return new(roots, enabled);
        }
        finally { _gate.Release(); }
    }
    public async Task<ManagedPluginInventory> ReadInventoryAsync(PlatformPluginHostSnapshot session, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(token).ConfigureAwait(false);
            var discovery = await new PlatformPluginCatalog(RootsFor(state), _compatibility).DiscoverAsync(token).ConfigureAwait(false);
            var items = new List<ManagedPluginItem>();
            foreach (var m in discovery.Plugins)
            {
                var trusted = await PlatformPluginIntegrity.VerifyAsync(m, token).ConfigureAwait(false);
                var enabled = state.Plugins.TryGetValue(m.Id, out var setting) && setting.Enabled;
                var active = session.Plugins.Any(p => p.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase));
                var same = session.Plugins.Any(p => p.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase) && p.ManifestPath == m.ManifestPath && p.Version == m.Version);
                var upgrade = m.SchemaVersion < _minimumManifestSchemaVersion;
                var status = !trusted ? "untrusted" : upgrade ? "upgradeRequired" : enabled ? same ? "enabled" : "enablePending" : active ? "disablePending" : "disabled";
                items.Add(new(m.Id, m.DisplayName, m.Version.ToString(), m.Providers.Select(p => p.DisplayName).ToArray(), status, enabled, active, trusted && !upgrade,
                    trusted && upgrade ? "manifestUpgradeRequired" : null));
            }
            // Incompatible packages remain explainable and can be disabled, but never enabled or routed.
            foreach (var diagnostic in discovery.Diagnostics)
            {
                var issue = PlatformHostCompatibility.ImportError(diagnostic.Code);
                if (issue is null || diagnostic.PluginId is not { } id || !IdPattern.IsMatch(id) || items.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                items.Add(new(id, id, "", [], "incompatible", state.Plugins.TryGetValue(id, out var preference) && preference.Enabled,
                    session.Plugins.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)), false, issue));
            }
            // Retain controllable disabled state even if a configured package disappears or becomes invalid.
            foreach (var id in state.Plugins.Keys.Concat(session.Plugins.Select(p => p.Id)).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!items.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    items.Add(new(id, id, "", [], "unavailable", state.Plugins.TryGetValue(id, out var setting) && setting.Enabled,
                        session.Plugins.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)), false));
            return new(items.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray(), discovery.Diagnostics
                .Where(d => d.Code != PlatformPluginDiagnosticCode.PluginDirectoryNotFound).Select(d => d.Code.ToString()).Distinct().ToArray());
        }
        finally { _gate.Release(); }
    }
    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken token = default)
    {
        ValidateId(id);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(token).ConfigureAwait(false);
            var discovery = await new PlatformPluginCatalog(RootsFor(state), _compatibility).DiscoverAsync(token).ConfigureAwait(false);
            var manifest = discovery.Plugins.SingleOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (enabled && manifest is not null && manifest.SchemaVersion < _minimumManifestSchemaVersion)
                throw new PlatformPluginCompatibilityException("manifestUpgradeRequired");
            if (enabled && (manifest is null || !await PlatformPluginIntegrity.VerifyAsync(manifest, token).ConfigureAwait(false)))
                throw new InvalidDataException("Untrusted plugin cannot be enabled.");
            if (manifest is null && !state.Plugins.ContainsKey(id)) throw new InvalidDataException("Unknown plugin.");
            state.Plugins[id] = new(enabled, state.Plugins.GetValueOrDefault(id)?.Package);
            await SaveStateAsync(state, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<PlatformPluginManifest?> FindManagedPluginAsync(string id, CancellationToken token)
    {
        ValidateId(id);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var discovery = await new PlatformPluginCatalog(RootsFor(await ReadStateAsync(token).ConfigureAwait(false)), _compatibility).DiscoverAsync(token).ConfigureAwait(false);
            return discovery.Plugins.SingleOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        finally { _gate.Release(); }
    }

    public async Task<PluginPackagePreview> PrepareImportAsync(string file, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CancelPrepared();
            _prepared = await PrepareCoreAsync(file, token).ConfigureAwait(false);
            return _prepared.Preview;
        }
        finally { _gate.Release(); }
    }

    private async Task<Prepared> PrepareCoreAsync(string file, CancellationToken token)
    {
        string? staging = null;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > Limit || info.Length == 0) throw new InvalidDataException("Invalid package size.");
            NoLinks(PackagesRoot);
            Directory.CreateDirectory(PackagesRoot);
            staging = Path.Combine(PackagesRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var payload = Path.Combine(staging, ".payload");
            Directory.CreateDirectory(payload);
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            stream.Position = 0;
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (archive.Entries.Count is < 2 or > 64) throw new InvalidDataException("Invalid file count.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var name = entry.FullName;
                    if (!FilePattern.IsMatch(name) || name.Contains("..", StringComparison.Ordinal) || !names.Add(name) ||
                        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                        throw new InvalidDataException("Invalid package entry.");
                    if (entry.Length > Limit || (total += entry.Length) > Limit) throw new InvalidDataException("Package too large.");
                    await using var source = entry.Open();
                    await using var destination = new FileStream(Path.Combine(payload, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[65536]; long copied = 0; int read;
                    while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        if ((copied += read) > entry.Length) throw new InvalidDataException("Invalid expanded size.");
                        await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                    if (copied != entry.Length) throw new InvalidDataException("Truncated entry.");
                }
            }
            var discovery = await new PlatformPluginCatalog([payload], _compatibility).DiscoverAsync(token).ConfigureAwait(false);
            if (discovery.Plugins.Count != 1 || discovery.Diagnostics.Count != 0)
            {
                var code = discovery.Diagnostics.Select(d => PlatformHostCompatibility.ImportError(d.Code)).FirstOrDefault(c => c is not null);
                if (code is not null) throw new PlatformPluginCompatibilityException(code);
                throw new InvalidDataException("Invalid manifest.");
            }
            var manifest = discovery.Plugins[0]; ValidateId(manifest.Id);
            if (manifest.SchemaVersion < _minimumManifestSchemaVersion)
                throw new PlatformPluginCompatibilityException("manifestUpgradeRequired");
            var finalPayload = Path.Combine(staging, manifest.Id);
            Directory.Move(payload, finalPayload);
            manifest = (await new PlatformPluginCatalog([finalPayload], _compatibility).DiscoverAsync(token).ConfigureAwait(false)).Plugins.Single();
            var hashes = await HashPayloadAsync(finalPayload, token).ConfigureAwait(false);
            var preview = new PluginPackagePreview(Guid.NewGuid().ToString("N"), manifest.Id, manifest.DisplayName,
                manifest.Version.ToString(), sha, manifest.Providers.Select(p => p.DisplayName).ToArray(),
                manifest.Providers.SelectMany(p => p.Capabilities).Select(c => c.ToString()).Distinct().ToArray(), manifest.CredentialAliases, manifest.HostRequirements);
            var prepared = new Prepared(staging, manifest, preview, hashes);
            staging = null;
            return prepared;
        }
        finally
        {
            if (staging is not null) RemoveOwnedStage(staging);
        }
    }

    public async Task<PluginImportBatchPreview> PrepareBatchAsync(IReadOnlyList<string> files, CancellationToken token = default)
    {
        if (files.Count is < 1 or > MaximumBatchCount) throw new InvalidDataException("Invalid batch size.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CancelPrepared();
            var rows = new List<PluginImportBatchItem>();
            long compressed = 0, expanded = 0;
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var name = new string(Path.GetFileName(file).Where(c => !char.IsControl(c)).Take(180).ToArray());
                Prepared? prepared = null;
                try
                {
                    var extension = Path.GetExtension(file);
                    if (extension is null || !(extension.Equals(".auralis-plugin", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException();
                    var size = new FileInfo(file).Length;
                    if (size > BatchLimit - compressed) { rows.Add(new(name, null, "batchLimit")); continue; }
                    compressed += size;
                    prepared = await PrepareCoreAsync(file, token).ConfigureAwait(false);
                    var sizeExpanded = Directory.GetFiles(prepared.Manifest.PluginDirectory).Sum(p => new FileInfo(p).Length);
                    if (sizeExpanded > BatchLimit - expanded)
                    {
                        RemoveOwnedStage(prepared.Directory); prepared = null;
                        rows.Add(new(name, null, "batchLimit")); continue;
                    }
                    expanded += sizeExpanded;
                    _batch.Add((name, prepared));
                    rows.Add(new(name, prepared.Preview, null));
                    prepared = null;
                }
                catch (PlatformPluginCompatibilityException ex)
                {
                    rows.Add(new(name, null, ex.Code));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    rows.Add(new(name, null, "invalidPackage"));
                }
                finally { if (prepared is not null) RemoveOwnedStage(prepared.Directory); }
            }
            // Reject all revisions of a duplicate ID, rather than silently selecting one by file order.
            var duplicates = _batch.GroupBy(p => p.Package.Manifest.Id, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].Preview is { } p && duplicates.Contains(p.Id)) rows[i] = rows[i] with { Preview = null, Error = "duplicatePlugin" };
            foreach (var item in _batch.Where(p => duplicates.Contains(p.Package.Manifest.Id)).ToArray())
            {
                RemoveOwnedStage(item.Package.Directory); _batch.Remove(item);
            }
            _batchPreview = new(Guid.NewGuid().ToString("N"), rows);
            return _batchPreview;
        }
        catch { CancelPrepared(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PluginImportResult>> ConfirmBatchAsync(string previewToken, bool trust, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!trust || _batchPreview?.Token != previewToken || _batch.Count == 0) throw new InvalidDataException("Explicit approval required.");
            var state = await ReadStateAsync(token).ConfigureAwait(false);
            var results = _batchPreview.Items.Where(p => p.Error is not null).Select(p => new PluginImportResult(p.FileName, null, p.Error)).ToList();
            var ready = new List<(string FileName, Prepared Package)>();
            foreach (var item in _batch)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var hashes = await HashPayloadAsync(item.Package.Manifest.PluginDirectory, token).ConfigureAwait(false);
                    if (hashes.Count != item.Package.Hashes.Count || hashes.Any(p => !item.Package.Hashes.TryGetValue(p.Key, out var h) || h != p.Value))
                        throw new InvalidDataException();
                    var approvals = Path.Combine(item.Package.Directory, ".approvals"); NoLinks(approvals);
                    Directory.CreateDirectory(approvals);
                    var receipt = Path.Combine(approvals, item.Package.Manifest.Id + ".json"); NoLinks(receipt);
                    await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new PluginInstallReceipt(2, item.Package.Manifest.Id, hashes, item.Package.Manifest.CredentialAliases)), token).ConfigureAwait(false);
                    state.Plugins[item.Package.Manifest.Id] = new(false, Path.GetFileName(item.Package.Directory));
                    ready.Add(item);
                    results.Add(new(item.FileName, item.Package.Manifest.Id, null));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                { results.Add(new(item.FileName, item.Package.Manifest.Id, "invalidPackage")); }
            }
            // No visible changes until every candidate has been checked. One atomic registry write.
            if (ready.Count > 0) await SaveStateAsync(state, token).ConfigureAwait(false);
            foreach (var item in ready) _batch.Remove(item);
            // Failed staged payloads never enter discovery. Cleanup failure must not turn committed
            // successes into an ambiguous "nothing imported" response (e.g. a locked invalid file).
            foreach (var item in _batch.ToArray())
            {
                try { RemoveOwnedStage(item.Package.Directory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                _batch.Remove(item);
            }
            _batchPreview = null;
            return results;
        }
        finally { _gate.Release(); }
    }
    private static async Task<Dictionary<string, string>> HashPayloadAsync(string folder, CancellationToken token)
    {
        NoLinks(folder);
        if (Directory.EnumerateDirectories(folder).Any()) throw new InvalidDataException("Unexpected directory.");
        var files = Directory.GetFiles(folder);
        if (files.Length is < 2 or > 64) throw new InvalidDataException("Invalid payload.");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var file in files)
        {
            NoLinks(file);
            if ((total += new FileInfo(file).Length) > Limit) throw new InvalidDataException("Payload too large.");
            await using var stream = File.OpenRead(file);
            hashes.Add(Path.GetFileName(file), Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)));
        }
        return hashes;
    }
    public async Task ConfirmImportAsync(string previewToken, bool trust, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var prepared = _prepared;
            if (!trust || prepared is null || prepared.Preview.Token != previewToken) throw new InvalidDataException("Explicit approval required.");
            var hashes = await HashPayloadAsync(prepared.Manifest.PluginDirectory, token).ConfigureAwait(false);
            if (hashes.Count != prepared.Hashes.Count || hashes.Any(p => !prepared.Hashes.TryGetValue(p.Key, out var h) || h != p.Value))
                throw new InvalidDataException("Package changed after preview.");
            var state = await ReadStateAsync(token).ConfigureAwait(false);
            var approvals = Path.Combine(prepared.Directory, ".approvals"); NoLinks(approvals);
            Directory.CreateDirectory(approvals);
            var receipt = Path.Combine(approvals, prepared.Manifest.Id + ".json"); NoLinks(receipt);
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new PluginInstallReceipt(2, prepared.Manifest.Id, hashes, prepared.Manifest.CredentialAliases)), token).ConfigureAwait(false);
            // Publication is one atomic registry replacement. Until this succeeds the package is invisible.
            // Existing immutable revisions remain intact for manual rollback; updates never inherit enablement.
            state.Plugins[prepared.Manifest.Id] = new(false, Path.GetFileName(prepared.Directory));
            await SaveStateAsync(state, token).ConfigureAwait(false);
            _prepared = null;
        }
        finally { _gate.Release(); }
    }
    public async Task CancelImportAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { CancelPrepared(); } finally { _gate.Release(); }
    }
    private void CancelPrepared()
    {
        _batchPreview = null;
        foreach (var item in _batch.ToArray()) { RemoveOwnedStage(item.Package.Directory); _batch.Remove(item); }
        if (_prepared is not { } p) return;
        _prepared = null;
        RemoveOwnedStage(p.Directory);
    }
    private void RemoveOwnedStage(string directory)
    {
        if (Path.GetDirectoryName(directory) != PackagesRoot || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            throw new InvalidDataException("Invalid staging root.");
        NoLinks(directory);
        if (!Directory.Exists(directory)) return;
        // Delete only this uncommitted, generated staging tree. Never follow links or touch source archives.
        void CheckTree(string parent)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                NoLinks(entry);
                if (Directory.Exists(entry)) CheckTree(entry);
            }
        }
        CheckTree(directory);
        Directory.Delete(directory, recursive: true);
    }
}
