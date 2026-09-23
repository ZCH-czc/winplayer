using System.Net;
using Auralis.MediaTransport;

internal static class CacheCleanupTests
{
    // Deliberately hold a Windows file lock after releasing the transport pin, modelling an
    // external scanner/late decoder handle. No sleeps, directory polling or retry-until-green.
    internal static async Task<int> RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cleanup lock fixture requires Windows.");
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; }
        for (var run = 0; run < 10; run++)
        foreach (var trigger in new[] { "release", "shutdown" })
        {
            var directory = Path.Combine(root, $"locked-eviction-{trigger}-{run}");
            var session = new HttpMediaTransportSession(directory, new Handler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) })));
            var pins = new List<IMediaTransportResource>();
            FileStream? externalReader = null;
            MediaTransportRequest Request(string name) => new(new Uri("https://cleanup.invalid/" + name),
                null, "audio/mpeg", "test", useHostTransport: true);
            try
            {
                var first = await session.PrepareAsync(Request("first"), null, default);
                var ownedPath = first.Source.LocalPath;
                externalReader = new FileStream(ownedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await first.DisposeAsync();
                for (var i = 0; i < 8; i++) pins.Add(await session.PrepareAsync(Request("pressure-" + i), null, default));
                Check(File.Exists(ownedPath), "controlled sharing lock actually prevents first eviction");
                Check(pins.All(p => File.Exists(p.Source.LocalPath)), "failed eviction preserves every live pin");
                externalReader.Dispose(); externalReader = null;

                if (trigger == "release")
                {
                    await pins[^1].DisposeAsync();
                    Check(!File.Exists(ownedPath), "next trim retries the previously blocked owned file");
                    Check(pins.Take(7).All(p => File.Exists(p.Source.LocalPath)), "retry does not delete unrelated live pins");
                }
                await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
                Check(!File.Exists(ownedPath), "shutdown must not forget a failed eviction after lock release");
                Check(pins.All(p => !File.Exists(p.Source.LocalPath)), "shutdown removes all prepared owned sources");
                await Task.WhenAll(pins.Select(p => p.DisposeAsync().AsTask()));
            }
            finally
            {
                externalReader?.Dispose();
                await session.DisposeAsync();
                await Task.WhenAll(pins.Select(p => p.DisposeAsync().AsTask()));
            }
        }
        Console.WriteLine($"PASS cache cleanup: {checks} assertions; 20 controlled-lock lifecycles, eviction/release/shutdown, independent pins, no polling or automatic test retries.");
        return checks;
    }
}
