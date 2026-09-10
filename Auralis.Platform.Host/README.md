# Auralis.Platform.Host

Host SDK 2.0.0 supports schema 5 explicit per-provider `commentArtworkDomains` and `comment-artwork.v1`, plus schema 4 host requirements.
Current Auralis and the default plugin manager require schema 5 for activation/import. Installed older packages remain visible
as `upgradeRequired`; an explicit reviewed upgrade remains OFF until enabled and restarted. Old state/accounts are not rewritten.
The catalog can still inspect schemas 1–4 inertly for migration tools. Generic Host options default to schema 1 for controlled
embedding/tests; the player and contract verification explicitly set `MinimumRuntimeVersion` (5), including unpacked roots.
There are no implicit platform credential/image tables: old undeclared images are deny-all, all credential aliases require declarations.
Removing the public `CompatiblePlatformCredentialStore` and tightening import defaults is a major SDK change, not a provider API change.
New plugins should use the [schema 5 example and feature matrix](../docs/PLUGIN_COMPATIBILITY.md), rather than copying the legacy example below.
API 1 and Abstractions 1.2.0 remain unchanged; assembly identity alone is not an SDK feature check.

Schema 3 adds exact `credentialAliases` metadata, validated without credential access. The player's import
approval receipt records these grants separately from file hashes. At activation the optional host-side
`IManifestPlatformHostContextFactory` receives the trusted manifest; provider ABI remains unchanged.
All contexts use `DeclaredPlatformCredentialStore`, not platform-ID aliases. See [credential lifecycle](../docs/PLUGIN_CREDENTIALS.md).

The desktop app now uses explicit bundled and per-user roots and requires local install approval through
`PlatformPluginIntegrity.VerifyAsync`. Checks occur at discovery and immediately before activation.
The Host SDK accepts a configurable trust policy; omitting it is for controlled embedding/tests, not safe
loading of arbitrary downloads. Checksums do not authenticate a publisher and in-process plugins are not sandboxed.
The default player build/publish does not reference or copy concrete provider implementations.

Dependency-free host for optional Auralis online-platform plugins. Constructing the host is inert. `DiscoverAsync`
reads manifests only; it does not load a DLL, create an HTTP client, construct a plugin, or contact the network.
The first routed capability call activates only the plugin that owns the requested provider.

## Historical manifest schema 1 (inspection only in current Auralis)

Each plugin occupies one folder with an entry assembly and `platform.plugin.json`:

```json
{
  "schemaVersion": 1,
  "id": "example.online-sources",
  "displayName": "Example online sources",
  "version": "1.0.0",
  "minimumHostApiVersion": 1,
  "maximumHostApiVersion": 1,
  "entryAssembly": "Example.Platform.dll",
  "entryType": "Example.Platform.OnlineSourcesPlugin",
  "providers": [
    {
      "id": "example",
      "displayName": "Example Music",
      "capabilities": ["TrackSearch", "Lyrics"]
    }
  ]
}
```

`assembly` and `entryPoint` are accepted aliases for `entryAssembly` and `entryType`. Provider IDs are compared
with `OrdinalIgnoreCase`. If an ID appears more than once, no conflicting declaration wins: every conflicting
provider is rejected and a diagnostic is recorded.

Plugin code runs in-process and must be trusted. The collectible load context and scoped services are lifecycle
and compatibility boundaries, not an operating-system security sandbox.
