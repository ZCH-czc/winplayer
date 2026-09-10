using System.Diagnostics;
using System.Text;
using Auralis.Playback.Host;
using Auralis.Services;

if (args.Length is not (3 or 5) || args[0] is not ("--run" or "--run-archive") || args[2] != "--trust-plugin-code" ||
    (args.Length == 5 && args[3] != "--install-root") || (args[0] == "--run-archive" && args.Length != 5))
{
    Console.WriteLine("Use --run <component directory> --trust-plugin-code [--install-root <NEW directory>], or --run-archive <ZIP> --trust-plugin-code --install-root <NEW directory>. Silent generated media only.");
    return 2;
}
var root = Path.Combine(Path.GetTempPath(), "Auralis-package-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
try
{
    Check(!File.Exists(Path.Combine(AppContext.BaseDirectory, "Auralis.Playback.LibVlc.dll")) &&
        !Directory.Exists(Path.Combine(AppContext.BaseDirectory, "libvlc")), "test executable has no bundled decoder to fall back to");
    PlaybackPackageSnapshot package;
    PlaybackComponentRegistration registration;
    if (args.Length == 5)
    {
        var installRoot = Path.GetFullPath(args[4]);
        if (Directory.Exists(installRoot) || File.Exists(installRoot)) throw new InvalidOperationException("Installation smoke requires a NEW directory.");
        var installer = new PlaybackInstallationStore(installRoot, PlaybackCapabilities.CompletePlayer);
        PlaybackInstalledRevision installed;
        if (args[0] == "--run-archive")
        {
            await using (var preview = await installer.PreviewArchiveAsync(args[1]))
            {
                Check(preview.FileCount > 3 && preview.ArchiveSha256.Length == 64, "real archive verified without activation");
                installed = await installer.ImportApprovedAsync(preview, new(preview.Descriptor.Id, preview.ManifestSha256));
            }
            Check(!Directory.EnumerateFileSystemEntries(Path.Combine(installRoot, "previews")).Any(), "real archive preview released after import");
        }
        else
        {
            var preview = await installer.PreviewAsync(args[1]);
            installed = await installer.ImportApprovedAsync(preview, new(preview.Descriptor.Id, preview.ManifestSha256));
        }
        Check(!installed.Enabled && installer.ReadStartupPlan().Registrations.Count == 0, "actual decoder import defaults off");
        await installer.SetEnabledAsync(installed.ComponentId, true);
        await installer.SelectAsync(installed.ComponentId);
        var plan = new PlaybackInstallationStore(installRoot, PlaybackCapabilities.CompletePlayer).ReadStartupPlan();
        Check(plan.Issue == PlaybackInstallationIssue.None && plan.SelectedId == installed.ComponentId && plan.Registrations.Count == 1,
            "actual decoder receipt survives reopening the store");
        registration = plan.Registrations.Single();
        package = PlaybackComponentCatalog.Discover(Path.Combine(installRoot, "revisions", installed.ManifestSha256),
            PlaybackCapabilities.CompletePlayer).Single().Package!;
    }
    else
    {
        var finding = PlaybackComponentCatalog.Discover(args[1], PlaybackCapabilities.CompletePlayer).Single();
        Check(finding.Issue == PlaybackPackageIssue.None, "compatible package");
        package = finding.Package!;
        registration = PlaybackPackageLoader.CreateRegistration(package, new(package.Manifest.Descriptor.Id, package.ManifestSha256), true);
    }
    var host = new PlaybackComponentRegistry([registration], registration.Descriptor.Id);
    Check(host.Inspect(PlaybackCapabilities.CompletePlayer).Single().Issue == PlaybackComponentIssue.None, "inert registry");
    var wave = Path.Combine(root, "silent.wav");
    using (var writer = new BinaryWriter(File.Create(wave), Encoding.ASCII))
    {
        const int rate = 44100, size = rate * 8 * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(size + 36);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(size);
        writer.Write(new byte[size]);
    }
    var avi = Path.Combine(root, "synthetic.avi"); SyntheticVideo.Write(avi);
    // Same identity as the bundled engine: selection must use the explicit installed source.
    var fallback = new PlaybackComponentRegistration(registration.Descriptor, true,
        () => throw new InvalidOperationException("Bundled sentinel must not execute in this test."));
    var composition = new PlaybackComponentComposition(new(PlaybackInstallationIssue.None, [registration],
        registration.Descriptor.Id, []), fallback, PlaybackCapabilities.CompletePlayer);
    composition.VerifyRuntime();
    using var player = composition.Create();
    Check(!composition.UsingBundled && !composition.FellBack, "production composition selects the actual package despite same-ID fallback");
    player.Volume = 0; player.SpeedRatio = 1.25;
    var failed = 0; var opened = 0; var ended = 0;
    player.MediaFailed += (_, _) => Interlocked.Increment(ref failed);
    player.MediaOpened += (_, _) => Interlocked.Increment(ref opened);
    player.MediaEnded += (_, _) => Interlocked.Increment(ref ended);
    player.Open(new Uri(wave), autoplay: false, positionSeconds: 1.5);
    await Wait(() => Volatile.Read(ref opened) > 0, "paused open");
    Check(!player.WantsPlayback && player.Position.TotalSeconds >= 1.4, "paused intent and position");
    player.Play(); await Wait(() => player.IsPlaying && player.Position.TotalSeconds > 1.7, "play");
    player.Pause(); await Wait(() => !player.IsPlaying, "pause");
    var paused = player.Position; await Task.Delay(200);
    Check(Math.Abs((player.Position - paused).TotalSeconds) < .2, "paused clock");
    for (var cycle = 0; cycle < 2; cycle++)
    {
        player.Position = TimeSpan.FromSeconds(7.5); player.Play();
        await Wait(() => Volatile.Read(ref ended) > cycle, "natural end");
        player.Restart(); await Wait(() => player.IsPlaying && player.Position.TotalSeconds is > .1 and < 2, "restart");
    }
    for (var cycle = 0; cycle < 3; cycle++)
    {
        player.Close(); Check(player.ReadVideoFrame() is null, "close clears frame");
        player.Open(new Uri(avi), true, 1, true, new Uri(wave));
        await Wait(() => player.ReadVideoFrame() is { Length: > 100 } && player.Position.TotalSeconds > 1.2, "video with audio slave");
        using (var frame = System.Drawing.Image.FromStream(new MemoryStream(player.ReadVideoFrame()!)))
            Check(frame.Width == 160 && frame.Height == 90, "decoded frame dimensions");
        player.Close(); player.Open(new Uri(wave), true, 1);
        await Wait(() => player.IsPlaying && player.Position.TotalSeconds > 1.2, "return to audio");
        Check(player.ReadVideoFrame() is null && player.Volume == 0 && player.SpeedRatio == 1.25, "no stale video, retained profile");
    }
    var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Where(m => m.ModuleName is "libvlc.dll" or "libvlccore.dll").ToArray();
    Check(modules.Length == 2 && modules.All(m => m.FileName.StartsWith(package.DirectoryPath + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase)), "native modules actually came from the component package");
    Check(Volatile.Read(ref failed) == 0, "no decoder failure");
    Console.WriteLine($"PASS dynamic playback package: {checks} assertions; no decoder in test output, shared contract, package-local native modules, silent WAV/AVI, repeat and three A/V cycles. Not UI/accounts/acoustic sync.");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("FAIL dynamic playback package: " + e.GetType().Name +
        (e is PlaybackComponentUnavailableException failure ? " / " + string.Join(",", failure.Issues) : ""));
    return 1;
}
finally { Directory.Delete(root, true); }
void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
async Task Wait(Func<bool> predicate, string label)
{
    var clock = Stopwatch.StartNew();
    while (!predicate())
    {
        NativeWindowGuard.AssertNoOwnedVisibleWindow();
        if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(label);
        await Task.Delay(25);
    }
    NativeWindowGuard.AssertNoOwnedVisibleWindow();
}
