using System.Text.Json.Nodes;

namespace Auralis.Platform.Host.Tests;

internal static class CommentArtworkPolicyTests
{
    internal static async Task RunAsync(string root)
    {
        var checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); checks++; }
        Check(PlatformCommentArtworkPolicy.TryCreate([], out var empty) && !empty.Allows(new Uri("https://cdn.example.test/a")), "Empty means deny");
        foreach (var domains in new string[]?[] { null, ["*"], ["*.example.test"], ["https://example.test"],
            ["localhost"], ["example.local"], ["x.localhost"], ["127.0.0.1"], ["[::1]"], ["example.test:443"],
            ["example.test/path"], ["user@example.test"], ["example.test?secret=x"], ["example.test#secret"],
            ["Example.test"], ["example.test."], ["example..test"], ["-bad.test"], ["bad-.test"], ["bad_name.test"],
            ["例.test"], [""], [null!], ["example.test", "example.test"],
            [new string('a',64) + ".test"], Enumerable.Range(0,17).Select(i => $"cdn{i}.test").ToArray() })
            Check(!PlatformCommentArtworkPolicy.TryCreate(domains, out _), "Unsafe or unbounded declaration rejected");
        var original = new[] { "cdn.example.test" };
        Check(PlatformCommentArtworkPolicy.TryCreate(original, out var policy), "Safe declaration accepted");
        original[0] = "other.test";
        Check(policy.Domains.Single() == "cdn.example.test", "Policy owns immutable declaration");
        foreach (var url in new[] { "https://cdn.example.test/a", "https://a.cdn.example.test/a", "https://CDN.EXAMPLE.TEST:443/a" })
            Check(policy.Allows(new Uri(url)), "Declared HTTPS host allowed");
        foreach (var url in new[] { "http://cdn.example.test/a", "https://cdn.example.test.evil.test/a",
            "https://evilcdn.example.test/a", "https://example.test/a", "https://user@cdn.example.test/a",
            "https://cdn.example.test:444/a", "https://cdn.example.test./a", "file:///a", "https://127.0.0.1/a",
            "https://cdn.example.test/" + new string('a',8192) })
            Check(!policy.Allows(new Uri(url)), "URI outside authorization denied");
        Check(!policy.Allows(null) && !policy.Allows(new Uri("relative", UriKind.Relative)), "Missing URI denied");

        foreach (var (target, success) in new[] {
            ("https://a.cdn.example.test/next", true), ("https://other.test/next", false),
            ("https://cdn.example.test.evil.test/next", false) })
        {
            var handler = new ImageHandler(request => request.RequestUri!.AbsolutePath == "/start"
                ? new(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri(target) } } : Image());
            using var source = new Auralis.Artwork.HttpArtworkSource(handler);
            try
            {
                var image = await source.FetchAsync(new(new Uri("https://cdn.example.test/start"), 1024, policy.Allows));
                Check(success && image.Length == 1, "Declared redirect yields independent payload");
            }
            catch (Auralis.Artwork.ArtworkException e)
            { Check(!success && e.Failure == Auralis.Artwork.ArtworkFailure.DestinationDenied && e.InnerException is null, "Foreign redirect denied safely"); }
            Check(handler.Requests == (success ? 2 : 1), "Denied destination never reaches handler");
        }
        var active = true;
        var lateHandler = new ImageHandler(_ => { active = false; return Image(); });
        using (var source = new Auralis.Artwork.HttpArtworkSource(lateHandler))
        {
            try
            {
                await source.FetchAsync(new(new Uri("https://cdn.example.test/a"), 1024, uri => active && policy.Allows(uri)));
                throw new InvalidOperationException("Revoked image returned");
            }
            catch (Auralis.Artwork.ArtworkException e)
            { Check(e.Failure == Auralis.Artwork.ArtworkFailure.DestinationDenied, "Late authorization revoked before payload delivery"); }
        }

        Directory.CreateDirectory(root);
        // Deliberately invalid CLR bytes: successful catalog discovery must not load them.
        await File.WriteAllBytesAsync(Path.Combine(root,"Fixture.dll"), [1,2,3]);
        var path = Path.Combine(root,"platform.plugin.json");
        var valid = JsonNode.Parse("""
            {"schemaVersion":5,"id":"tests.artwork","displayName":"Fixture","version":"1.0.0",
             "minimumHostApiVersion":1,"maximumHostApiVersion":1,"entryAssembly":"Fixture.dll","entryType":"Fixture.Plugin",
             "hostRequirements":{"minimumHostSdkVersion":"1.2.0","requiredFeatures":["comment-artwork.v1"]},
             "providers":[{"id":"fixture","displayName":"Fixture","capabilities":["Comments"],"commentArtworkDomains":["cdn.example.test"]}]}
            """)!.AsObject();
        async Task<PlatformPluginDiscoveryResult> Discover(JsonObject json, PlatformHostCompatibility? profile = null)
        {
            await File.WriteAllTextAsync(path, json.ToJsonString());
            return await new PlatformPluginCatalog([root], profile).DiscoverAsync();
        }
        var found = (await Discover(valid)).Plugins.Single().Providers.Single();
        Check(!found.UsesLegacyCommentArtworkPolicy && found.CommentArtworkPolicy.Allows(new Uri("https://cdn.example.test/a")), "Schema 5 is declarative and inert");
        Check(!found.CommentArtworkPolicy.Allows(new Uri("https://i0.hdslb.com/a")), "No implicit legacy domains for new schema");
        foreach (var mutate in new Action<JsonObject>[] {
            j => j["providers"]![0]!.AsObject().Remove("commentArtworkDomains"),
            j => j["providers"]![0]!["commentArtworkDomains"] = null,
            j => j["providers"]![0]!["commentArtworkDomains"] = new JsonArray("*"),
            j => j["hostRequirements"]!["requiredFeatures"] = new JsonArray(),
            j => j["schemaVersion"] = 4 })
        {
            var test = (JsonObject)valid.DeepClone(); mutate(test);
            var rejected = await Discover(test);
            Check(rejected.Plugins.Count == 0 && rejected.Diagnostics.Any(d => d.Code == PlatformPluginDiagnosticCode.ManifestInvalid), "Missing/invalid/cross-schema policy rejected");
        }
        var deny = (JsonObject)valid.DeepClone(); deny["providers"]![0]!["commentArtworkDomains"] = new JsonArray();
        Check((await Discover(deny)).Plugins.Single().Providers.Single().CommentArtworkPolicy.Domains.Count == 0, "Explicit empty schema 5 declaration retained");
        var oldHost = new PlatformHostCompatibility(new Version(1,1,0), [],4);
        Check((await Discover(valid, oldHost)).Diagnostics.Any(d => d.Code == PlatformPluginDiagnosticCode.ManifestSchemaUnsupported), "Old host rejects new schema before loading");
        for (var schema=1; schema<=4; schema++)
        {
            var legacy = (JsonObject)valid.DeepClone(); legacy["schemaVersion"] = schema;
            legacy["providers"]![0]!.AsObject().Remove("commentArtworkDomains");
            if(schema<4) legacy.Remove("hostRequirements");
            var provider = (await Discover(legacy)).Plugins.Single().Providers.Single();
            Check(provider.UsesLegacyCommentArtworkPolicy && provider.CommentArtworkPolicy.Domains.Count == 0 &&
                !provider.CommentArtworkPolicy.Allows(new Uri("https://cdn.example.test/a")), "Undeclared legacy artwork is denied, never filled from a host table");
        }
        Console.WriteLine($"PASS {checks} comment artwork policy checks (declarations, URI boundaries, inert discovery, legacy compatibility).");
    }

    private static HttpResponseMessage Image()
    {
        var result = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        result.Content.Headers.ContentType = new("image/png");
        return result;
    }
    private sealed class ImageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; return Task.FromResult(respond(request)); }
    }
}
