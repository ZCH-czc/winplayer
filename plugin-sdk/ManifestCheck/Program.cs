using System.Text.Json;
using Auralis.Platform.Host;

// Uses the old metadata surface, not the newest optional capability interfaces.
// The selected trusted host SDK runs here; provider DLLs are never loaded.
if (args.Length != 1) return 2;
try
{
    var profile = PlatformHostCompatibility.Current;
    var directory = Path.GetFullPath(args[0]);
    if (!Directory.Exists(directory)) throw new IOException();
    using var input = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "platform.plugin.json")));
    var manifest = input.RootElement;
    var reasons = new List<string>();
    var requirements = manifest.GetProperty("hostRequirements").Deserialize<PlatformHostRequirements>()!;
    if (profile.Check(requirements) is { } code) reasons.Add(code.ToString());
    var schema = manifest.GetProperty("schemaVersion").GetInt32();
    if (schema < PlatformPluginManifestSchema.MinimumRuntimeVersion) reasons.Add("ManifestUpgradeRequired");
    if (schema > profile.MaximumManifestSchema) reasons.Add("ManifestSchemaUnsupported");
    // An old parser need not understand new page fields to reject a declared SDK mismatch.
    if (reasons.Count == 0)
    {
        var catalog = await new PlatformPluginCatalog([directory]).DiscoverAsync();
        reasons.AddRange(catalog.Diagnostics.Select(d => d.Code.ToString()));
        if (catalog.Plugins.Count != 1) reasons.Add("ExpectedOnePlugin");
    }
    Console.WriteLine(JsonSerializer.Serialize(new {
        Status = reasons.Count == 0 ? "compatible" : "incompatible", SdkVersion=profile.Version.ToString(),
        Features=profile.Features.Order().ToArray(), MaximumManifestSchema=profile.MaximumManifestSchema,
        Reasons=reasons.Distinct().ToArray(), PluginCodeExecuted=false, StaticOnly=true
    }));
    return reasons.Count == 0 ? 0 : 1;
}
catch
{
    Console.WriteLine("{\"Status\":\"check-failed\",\"Reasons\":[\"MetadataCheckFailed\"],\"PluginCodeExecuted\":false,\"StaticOnly\":true}");
    return 2;
}
