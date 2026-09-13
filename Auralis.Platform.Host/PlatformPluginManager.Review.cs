using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Auralis.Platform.Host;

// Safe metadata only. Neither this review nor checksum approval certifies business functionality.
public sealed record PluginDeclarationChange(string Kind, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);
public sealed record PluginPackageReview(string Kind, string? PreviousVersion, string? PreviousPayloadSha256,
    bool PreviousVerified, string HostSdkVersion, bool AccessReviewRequired, IReadOnlyList<PluginDeclarationChange> Changes);

public sealed partial class PlatformPluginManager
{
    private sealed record ReviewBaseline(PlatformPluginManifest? Manifest, bool Verified, bool Selected,
        string? PayloadSha256, string Fingerprint);

    private static string PayloadDigest(Dictionary<string, string> hashes) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", hashes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}\t{p.Value}")))));

    private async Task<ReviewBaseline> ReadReviewBaselineAsync(string id, CancellationToken token)
    {
        var state = await ReadStateAsync(token).ConfigureAwait(false);
        var discovery = await new PlatformPluginCatalog(RootsFor(state), _compatibility).DiscoverAsync(token).ConfigureAwait(false);
        var old = discovery.Plugins.SingleOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var selected = state.Plugins.TryGetValue(id, out var preference) || old is not null;
        var digest = old is null ? null : PayloadDigest(await HashPayloadAsync(old.PluginDirectory, token).ConfigureAwait(false));
        var verified = old is not null && await PlatformPluginIntegrity.VerifyAsync(old, token).ConfigureAwait(false);
        // Only the selected plugin is bound: another independent plugin may be changed in the same batch.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { preference, path = old?.ManifestPath, digest, verified }))));
        return new(old, verified, selected, digest, fingerprint);
    }

    private async Task ValidateReviewBaselineAsync(Prepared prepared, CancellationToken token)
    {
        var current = await ReadReviewBaselineAsync(prepared.Manifest.Id, token).ConfigureAwait(false);
        if (current.Fingerprint != prepared.Baseline) throw new InvalidDataException("Selection changed after review. Select the package again.");
    }

    private PluginPackageReview BuildReview(ReviewBaseline baseline, PlatformPluginManifest candidate)
    {
        var old = baseline.Manifest;
        var changes = new List<PluginDeclarationChange>();
        void Diff(string kind, IEnumerable<string>? before, IEnumerable<string> after)
        {
            var previous = (before ?? []).ToHashSet(StringComparer.Ordinal);
            var next = after.ToHashSet(StringComparer.Ordinal);
            var added = next.Except(previous).Order(StringComparer.Ordinal).ToArray();
            var removed = previous.Except(next).Order(StringComparer.Ordinal).ToArray();
            if (added.Length + removed.Length > 0) changes.Add(new(kind, added, removed));
        }
        static IEnumerable<string> Capabilities(PlatformPluginManifest m) => m.Providers.SelectMany(p => p.Capabilities.Select(c => $"{p.Id} / {c}"));
        static IEnumerable<string> Pages(PlatformPluginManifest m) => m.Providers.SelectMany(p => p.Pages.Select(e => $"{p.Id} / {e.Id} / v{e.DocumentVersion}"));
        static IEnumerable<string> Settings(PlatformPluginManifest m) => m.Providers.SelectMany(p => p.Settings.Select(s => $"{p.Id} / {s.Key} / {s.Kind}"));
        static IEnumerable<string> Credentials(PlatformPluginManifest m) => m.CredentialAliases.Select(a => $"{a.Key} / {a.Scope} / {a.LegacyKey}");
        static IEnumerable<string> Aliases(PlatformPluginManifest m) => m.Providers.SelectMany(p => p.Settings.SelectMany(s => s.LegacyKeys.Select(k => $"{p.Id} / {s.Key} / {k}")));
        static IEnumerable<string> Domains(PlatformPluginManifest m) => m.Providers.SelectMany(p => p.CommentArtworkPolicy.Domains.Select(d => $"{p.Id} / {d}"));
        Diff("providers", old?.Providers.Select(p => p.Id), candidate.Providers.Select(p => p.Id));
        Diff("capabilities", old is null ? null : Capabilities(old), Capabilities(candidate));
        Diff("pages", old is null ? null : Pages(old), Pages(candidate));
        Diff("settings", old is null ? null : Settings(old), Settings(candidate));
        Diff("features", old?.HostRequirements?.RequiredFeatures, candidate.HostRequirements?.RequiredFeatures ?? []);
        Diff("credentials", old is null ? null : Credentials(old), Credentials(candidate));
        Diff("settingAliases", old is null ? null : Aliases(old), Aliases(candidate));
        Diff("artworkDomains", old is null ? null : Domains(old), Domains(candidate));
        var kind = old is null ? baseline.Selected ? "unknown" : "install" : candidate.Version.CompareTo(old.Version) switch
        { > 0 => "update", < 0 => "recovery", _ => "reinstall" };
        return new(kind, old?.Version.ToString(), baseline.PayloadSha256, baseline.Verified, _compatibility.Version.ToString(),
            changes.Any(c => c.Kind is "credentials" or "settingAliases" or "artworkDomains"), changes);
    }

    /// <summary>Explicit file-picker recovery, not automatic rollback or enumeration of old user payloads.</summary>
    public async Task<PluginImportBatchPreview> PrepareRecoveryAsync(string file, string id, CancellationToken token = default)
    {
        ValidateId(id);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        Prepared? prepared = null;
        try
        {
            CancelPrepared();
            prepared = await PrepareCoreAsync(file, token).ConfigureAwait(false);
            if (!prepared.Manifest.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                prepared.Preview.Review is not { Kind: "recovery", PreviousVerified: true })
                throw new InvalidDataException("Recovery requires the same identity and a lower compatible version.");
            var name = new string(Path.GetFileName(file).Where(c => !char.IsControl(c)).Take(180).ToArray());
            _batch.Add((name, prepared));
            _batchPreview = new(Guid.NewGuid().ToString("N"), [new(name, prepared.Preview, null)]);
            prepared = null;
            return _batchPreview;
        }
        finally
        {
            if (prepared is not null) RemoveOwnedStage(prepared.Directory);
            _gate.Release();
        }
    }
}
