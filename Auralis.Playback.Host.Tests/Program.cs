using Auralis.Services;
using Auralis.Playback.Host;

if (args.Length == 2 && args[0] == "--loader-worker")
{
    try { LoaderTests.RunPackageTests(args[1]); return 0; }
    catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
}

try
{
var assertions = 0;
void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); assertions++; }
PlaybackComponentDescriptor Descriptor(string id) => new(id, "Synthetic", new Version(1, 0), 1,
    new Version(0, 1, 0), PlaybackCapabilities.CompletePlayer);
var baseline = Descriptor("fixture.default");
var candidate = Descriptor("fixture.optional");
var activated = 0;
var created = new List<Factory>();
PlaybackComponentRegistration Default() => new(baseline, true, () => { activated++; var f = new Factory(baseline); created.Add(f); return f; });
var registry = new PlaybackComponentRegistry([Default()], baseline.Id);
Check(registry.Inspect(PlaybackCapabilities.CompletePlayer)[0].Issue == PlaybackComponentIssue.None && activated == 0, "inert inspection");
var context = new SynchronizationContext();
var selected = registry.Create(null, PlaybackCapabilities.CompletePlayer, context);
Check(!selected.UsedFallback && selected.Issues.Count == 0 && activated == 1 && created[0].Context == context, "default/context");
using (var session = selected.Session)
{
    var raw = created[0].Session;
    session.Volume = .37; session.IsMuted = true; session.SpeedRatio = 1.5;
    Check(raw.Volume == .37 && raw.IsMuted && raw.SpeedRatio == 1.5, "profile forwarding");
    var opened = 0; EventHandler handler = (_, _) => opened++;
    session.MediaOpened += handler;
    session.Open(new Uri("https://example.invalid/synthetic"), false, 42, true, new Uri("https://example.invalid/audio"));
    Check(opened == 1 && raw.Position == TimeSpan.FromSeconds(42) && raw.Video && raw.Slave is not null && !raw.WantsPlayback, "open/events/independent audio");
    session.MediaOpened -= handler;
    session.Restart(); Check(opened == 1 && raw.Position == TimeSpan.Zero, "unsubscribe/restart");
    session.Pause(); Check(!session.IsPlaying, "pause");
    session.Play(); Check(session.WantsPlayback, "resume");
    session.Close(); Check(!session.WantsPlayback && session.ReadVideoFrame() is null, "close/frame");
}
selected.Session.Dispose(); selected.Session.Close();
Check(created[0].Session.DisposeCount == 1 && created[0].DisposeCount == 1 && selected.Session.ReadVideoFrame() is null, "idempotent ownership");
try { selected.Session.Play(); throw new Exception("disposed session played"); } catch (ObjectDisposedException) { assertions++; }
var second = registry.Create(null, PlaybackCapabilities.Audio);
Check(!ReferenceEquals(created[0].Session, created[1].Session), "independent sessions");
second.Session.Dispose();

foreach (var (metadata, enabled, expected) in new[]
{
    (candidate, false, PlaybackComponentIssue.Disabled),
    (candidate with { ContractApiVersion = 2 }, true, PlaybackComponentIssue.ApiMismatch),
    (candidate with { MinimumHostVersion = new Version(99, 0) }, true, PlaybackComponentIssue.HostTooOld),
    (candidate with { Capabilities = PlaybackCapabilities.Audio }, true, PlaybackComponentIssue.MissingCapability)
})
{
    var calls = 0;
    var host = new PlaybackComponentRegistry([Default(), new(metadata, enabled, () => { calls++; return new Factory(metadata); })], baseline.Id);
    var result = host.Create(candidate.Id, PlaybackCapabilities.CompletePlayer);
    using var session = result.Session;
    Check(result.UsedFallback && result.ComponentId == baseline.Id && result.Issues.Contains(expected) && calls == 0, "incompatible/disabled zero activation");
}
using (var unknown = registry.Create("fixture.missing", PlaybackCapabilities.Audio).Session) { Check(unknown is not null, "unknown falls back"); }
foreach (var phase in new[] { "activate", "descriptor", "verify", "create", "null" })
{
    Factory? bad = null;
    var host = new PlaybackComponentRegistry([Default(), new(candidate, true, () =>
    {
        if (phase == "activate") throw new Exception("must-not-leak-secret");
        bad = new Factory(phase == "descriptor" ? baseline : candidate) { Phase = phase };
        return bad;
    })], baseline.Id);
    var result = host.Create(candidate.Id, PlaybackCapabilities.CompletePlayer);
    using var session = result.Session;
    Check(result.UsedFallback && result.Issues.Count == 1, "failed candidate fallback");
    Check(bad is null || bad.DisposeCount == 1, "failed factory disposal");
}
var faulty = new Factory(candidate) { Phase = "create" };
try
{
    new PlaybackComponentRegistry([new(candidate, true, () => faulty)], candidate.Id).Create(null, PlaybackCapabilities.Audio);
    throw new Exception("empty success");
}
catch (PlaybackComponentUnavailableException e)
{
    Check(e.Issues.SequenceEqual([PlaybackComponentIssue.CreationFailed]) && !e.ToString().Contains("must-not-leak-secret"), "total failure typed/sanitized");
}
var shared = new Session();
var duplicateHost = new PlaybackComponentRegistry([Default(), new(candidate, true, () => new Factory(candidate) { Session = shared })], baseline.Id);
using (var first = duplicateHost.Create(candidate.Id, PlaybackCapabilities.Audio).Session)
{
    var result = duplicateHost.Create(candidate.Id, PlaybackCapabilities.Audio);
    using var fallback = result.Session;
    Check(result.UsedFallback && result.Issues.Contains(PlaybackComponentIssue.ReusedSession) && shared.DisposeCount == 0, "reused session rejected without disposal");
}
Check(shared.DisposeCount == 1, "original session retains ownership");
var disposalIssues = new List<PlaybackComponentIssue>();
var badDispose = new Factory(baseline) { ThrowDispose = true, Session = new Session { ThrowDispose = true } };
var disposing = new PlaybackComponentRegistry([new(baseline, true, () => badDispose)], baseline.Id, disposalIssues.Add)
    .Create(null, PlaybackCapabilities.Audio).Session;
disposing.Dispose();
Check(disposalIssues.SequenceEqual([PlaybackComponentIssue.SessionDisposeFailed, PlaybackComponentIssue.FactoryDisposeFailed]) &&
    badDispose.DisposeCount == 1, "both disposal paths attempted");
var throwingLogger = new PlaybackComponentRegistry([Default()], baseline.Id, _ => throw new Exception());
using (var fallback = throwingLogger.Create("missing", PlaybackCapabilities.Audio).Session) Check(fallback is not null, "diagnostic failure isolated");
try { _ = new PlaybackComponentRegistry([Default(), Default()], baseline.Id); throw new Exception("duplicate accepted"); }
catch (ArgumentException) { assertions++; }
try { registry.Create(null, (PlaybackCapabilities)1024); throw new Exception("invalid capability"); }
catch (ArgumentOutOfRangeException) { assertions++; }
var source = new List<PlaybackComponentRegistration> { Default() };
var snapshot = new PlaybackComponentRegistry(source, baseline.Id);
source.Clear();
Check(snapshot.Inspect(PlaybackCapabilities.Audio).Count == 1, "immutable registry snapshot");
var probeFactory = new Factory(baseline);
var probe = new PlaybackComponentRegistry([new(baseline, true, () => probeFactory)], baseline.Id);
Check(!probe.VerifyRuntime(null, PlaybackCapabilities.Audio).UsedFallback &&
    probeFactory.CreateCount == 0 && probeFactory.DisposeCount == 1, "preflight has no session/device");
var singleton = new Factory(candidate);
var reuseHost = new PlaybackComponentRegistry([Default(), new(candidate, true, () => singleton)], baseline.Id);
using (var original = reuseHost.Create(candidate.Id, PlaybackCapabilities.Audio).Session)
{
    var recovered = reuseHost.Create(candidate.Id, PlaybackCapabilities.Audio);
    using var recoverySession = recovered.Session;
    Check(recovered.Issues.Contains(PlaybackComponentIssue.ReusedFactory) && singleton.DisposeCount == 0, "reused factory cannot dispose owner");
}
var autoPlaying = new Factory(candidate); autoPlaying.Session.Play();
var unexpected = new PlaybackComponentRegistry([Default(), new(candidate, true, () => autoPlaying)], baseline.Id)
    .Create(candidate.Id, PlaybackCapabilities.Audio);
unexpected.Session.Dispose();
Check(unexpected.UsedFallback && autoPlaying.Session.DisposeCount == 1, "creation may not start audio");
var probeBad = new Factory(candidate) { ThrowDispose = true };
var probeFallback = new PlaybackComponentRegistry([Default(), new(candidate, true, () => probeBad)], baseline.Id)
    .VerifyRuntime(candidate.Id, PlaybackCapabilities.Audio);
Check(probeFallback.UsedFallback && probeFallback.Issues.Contains(PlaybackComponentIssue.FactoryDisposeFailed) &&
    probeBad.DisposeCount == 1, "failed preflight disposal rejects candidate once");
Console.WriteLine($"PASS playback host: {assertions} assertions; inert metadata, compatibility, capability selection, fallback, context/events, ownership and safe diagnostics. No native runtime or accounts.");
await CatalogTests.RunAsync();
await InstallationTests.RunAsync();
await ArchiveTests.RunAsync();
CompositionTests.Run();
LoaderTests.Run();
return 0;
}
catch (Exception failure)
{
    // A failing synthetic assertion must terminate, not leave an unhandled-exception dialog
    // holding the test apphost open and blocking the next build on Windows.
    Console.Error.WriteLine("FAIL playback fixture: " + failure.GetType().Name + ": " + failure.Message);
    return 1;
}

sealed class Factory(PlaybackComponentDescriptor descriptor) : IPlaybackComponentFactory
{
    public PlaybackComponentDescriptor Descriptor => descriptor;
    public string? Phase { get; init; }
    public Session Session { get; init; } = new();
    public SynchronizationContext? Context { get; private set; }
    public int DisposeCount { get; private set; }
    public int CreateCount { get; private set; }
    public bool ThrowDispose { get; init; }
    public void VerifyRuntime() { if (Phase == "verify") throw new Exception("must-not-leak-secret"); }
    public IPlaybackSession Create(SynchronizationContext? context)
    {
        Context = context;
        CreateCount++;
        if (Phase == "create") throw new Exception("must-not-leak-secret");
        return Phase == "null" ? null! : Session;
    }
    public void Dispose() { DisposeCount++; if (ThrowDispose) throw new Exception(); }
}
sealed class Session : IPlaybackSession
{
    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded { add { } remove { } }
    public event EventHandler<AudioPlaybackFailedEventArgs>? MediaFailed { add { } remove { } }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration => TimeSpan.FromMinutes(1);
    public double Volume { get; set; }
    public bool IsMuted { get; set; }
    public double SpeedRatio { get; set; }
    public bool IsPlaying => WantsPlayback;
    public bool WantsPlayback { get; private set; }
    public bool Video { get; private set; }
    public Uri? Slave { get; private set; }
    public int DisposeCount { get; private set; }
    public bool ThrowDispose { get; init; }
    public void Open(Uri source, bool autoplay = true, double positionSeconds = 0, bool video = false, Uri? audioSlave = null)
    { Position = TimeSpan.FromSeconds(positionSeconds); WantsPlayback = autoplay; Video = video; Slave = audioSlave; MediaOpened?.Invoke(this, EventArgs.Empty); }
    public void Play() => WantsPlayback = true;
    public void Pause() => WantsPlayback = false;
    public void Restart() { Position = TimeSpan.Zero; Play(); }
    public void Close() => Pause();
    public AudioPlaybackProfile CapturePlaybackProfile() => new(AudioOutputSettings.Default, Volume, SpeedRatio);
    public void ApplyAudioOutputSettings(AudioOutputSettings settings) { }
    public AudioDeviceSnapshot GetAudioDeviceSnapshot() => new([], [], AudioOutputSettings.Default, "", "", "", "", AudioEndpointLatencyInfo.Unavailable("fixture"));
    public AudioEndpointProbeRequest? GetAudioEndpointProbeRequest(AudioDeviceSnapshot snapshot) => null;
    public byte[]? ReadVideoFrame() => null;
    public void Dispose() { DisposeCount++; if (ThrowDispose) throw new Exception(); }
}
