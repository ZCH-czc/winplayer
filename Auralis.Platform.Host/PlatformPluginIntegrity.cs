using System.Security.Cryptography;
using System.Text.Json;

namespace Auralis.Platform.Host;

/// <summary>
/// Verifies the exact flat payload approved by the local plugin installer. Receipts live outside plugin
/// folders. This detects unapproved/changed packages; it is NOT publisher authentication or a sandbox
/// against another process running as the same Windows user. Approval requires explicit user trust.
/// </summary>
public static class PlatformPluginIntegrity
{
    /// <summary>Manifest and every deployed file must match an out-of-folder install receipt.</summary>
    public static async Task<bool> VerifyAsync(PlatformPluginManifest manifest, CancellationToken token)
    {
        try
        {
            var directory = new DirectoryInfo(manifest.PluginDirectory);
            if (directory.Name != manifest.Id || directory.Parent is null ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var receiptPath = Path.Combine(directory.Parent.FullName, ".approvals", manifest.Id + ".json");
            if ((directory.Parent.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var approvals = new DirectoryInfo(Path.GetDirectoryName(receiptPath)!);
            if (!approvals.Exists || (approvals.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var receiptInfo = new FileInfo(receiptPath);
            if (!receiptInfo.Exists || receiptInfo.Length > 64 * 1024 ||
                (receiptInfo.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            await using var receiptStream = receiptInfo.OpenRead();
            var receipt = await JsonSerializer.DeserializeAsync<PluginInstallReceipt>(receiptStream, cancellationToken: token).ConfigureAwait(false);
            if (receipt is null || receipt.SchemaVersion is not (1 or 2) || receipt.PluginId != manifest.Id ||
                receipt.Files is null || receipt.Files.Count is < 2 or > 64 ||
                !receipt.Files.ContainsKey("platform.plugin.json") || !receipt.Files.ContainsKey(manifest.EntryAssembly)) return false;
            // A package hash alone is not approval to access legacy account addresses. Require an
            // exact, separately recorded grant set from the import confirmation; never infer by ID.
            var grants = receipt.CredentialAliases ?? [];
            if (!PlatformCredentialAlias.IsValidList(grants) ||
                (receipt.SchemaVersion == 1 && (grants.Count > 0 || manifest.CredentialAliases.Count > 0)) ||
                grants.Count != manifest.CredentialAliases.Count ||
                !grants.ToHashSet().SetEquals(manifest.CredentialAliases)) return false;
            // Flat payloads deliberately exclude scripts, data profiles and arbitrary dependency trees.
            if (directory.EnumerateDirectories().Any()) return false;
            var files = directory.GetFiles();
            if (files.Length != receipt.Files.Count || files.Sum(file => file.Length) > 128L * 1024 * 1024) return false;
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (file.Length > 128L * 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !receipt.Files.TryGetValue(file.Name, out var expected) || expected.Length != 64) return false;
                await using var stream = file.OpenRead();
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}

/// <summary>Local approval receipt. Never contains cookies, credentials or user media.</summary>
public sealed record PluginInstallReceipt(int SchemaVersion, string PluginId, Dictionary<string, string> Files,
    IReadOnlyList<PlatformCredentialAlias>? CredentialAliases = null);
