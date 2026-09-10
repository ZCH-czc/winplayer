using Auralis.Services;
using Auralis.Playback.Host;
using System.Diagnostics;
using System.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Auralis.MediaTransport;
using Auralis.MediaTransport.Host;
using System.Net;
using System.Net.Http;

// Run native initialization in its own bounded process: an access violation cannot be caught.
if (args.Contains("--startup-probe", StringComparer.Ordinal))
{
    var probe = new Thread(() =>
    {
        if (!args.Contains("--native-mmdevice") && !args.Contains("--directsound"))
        {
            Console.WriteLine("PROBE production constructor / " + Thread.CurrentThread.GetApartmentState());
            using var player = new BundledAudioPlayer { Volume = 0 };
            Console.WriteLine("PROBE production snapshot and default settings");
            _ = player.GetAudioDeviceSnapshot();
            player.ApplyAudioOutputSettings(AudioOutputSettings.Default);
            Console.WriteLine("PROBE production dispose");
            return;
        }
        Console.WriteLine("PROBE Core.Initialize");
        Core.Initialize();
        var module = args.Contains("--directsound") ? "directsound" : "mmdevice";
        Console.WriteLine("PROBE LibVLC " + module);
        using var vlc = new LibVLC("--intf=dummy", "--quiet", "--aout=" + module);
        Console.WriteLine("PROBE MediaPlayer");
        using var native = new MediaPlayer(vlc);
        Console.WriteLine("PROBE Volume");
        native.Volume = 0;
        Console.WriteLine("PROBE Device enumeration");
        foreach (var output in LibVlcAudioOutputConfiguration.GetModules(vlc))
        {
            Console.WriteLine("PROBE devices " + output.Id);
            _ = vlc.AudioOutputDevices(output.Id)?.ToArray();
        }
        Console.WriteLine("PROBE Apply output settings");
        LibVlcAudioOutputConfiguration.ApplySelection(vlc, native, AudioOutputSettings.Default with { OutputModule = module });
        Console.WriteLine("PROBE Dispose");
    });
    probe.SetApartmentState(args.Contains("--sta") ? ApartmentState.STA : ApartmentState.MTA);
    probe.Start();
    if (!probe.Join(TimeSpan.FromSeconds(15))) Environment.Exit(3);
    Console.WriteLine("PASS startup probe");
    return 0;
}

// Explicit, local and silent: no profile, personal media, platform credentials, SMTC or window.
// Verifies real decoder controls and memory video frames, not speaker output or HWND composition.
if (!args.Contains("--run", StringComparer.Ordinal))
{
    Console.WriteLine("Use --run for a silent LibVLC smoke test with generated temporary WAV/AVI fixtures.");
    return 2;
}
var folder = Path.Combine(Path.GetTempPath(), "Auralis-engine-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var file = Path.Combine(folder, "synthetic.wav");
    using (var writer = new BinaryWriter(File.Create(file), Encoding.ASCII))
    {
        const int rate=44100, samples=rate*8, bytes=samples*2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(bytes+36);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate*2);
        writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
        for (var i=0;i<samples;i++) writer.Write((short)(Math.Sin(2*Math.PI*440*i/rate)*3000));
    }
    var generatedFile = file;
    var transportComposition = new MediaTransportComponentComposition(new(MediaTransportInstallationIssue.None, [], null, []),
        new(HttpMediaTransportFactory.Metadata,true,()=>new HttpMediaTransportFactory(()=>new GeneratedMediaHandler(generatedFile))), MediaTransportCapabilities.Full);
    await using IMediaTransportSession? transport = args.Contains("--transport")
        ? transportComposition.CreateDeferred(new(Path.Combine(folder,"transport"),new MediaTransferBudget())) : null;
    await using var prepared = transport is null ? null : await transport.PrepareAsync(new(new Uri("https://fixture.invalid/synthetic.wav"),
        DateTimeOffset.UtcNow.AddMinutes(5), "audio/wav", "fixture", useHostTransport:true), "fixture:wave", default);
    if (transport is not null)
    {
        Check(prepared!.Source.IsFile && prepared.Source.LocalPath != file, "separate transport component prepared a complete cache source");
        file = prepared.Source.LocalPath;
    }
    Console.WriteLine("CHECK production player construction");
    var factory = new CapturingPlaybackFactory();
    var registry = new PlaybackComponentRegistry(
        [new(factory.Descriptor, true, () => factory)], factory.Descriptor.Id);
    using IPlaybackSession player = registry.Create(null, PlaybackCapabilities.CompletePlayer).Session;
    player.Volume = 0;
    var outputSnapshot = player.GetAudioDeviceSnapshot();
    Check(outputSnapshot.Settings.OutputModule == "auto", "automatic preference remains automatic");
    var opened = 0; var failed = 0;
    player.MediaOpened += (_,_) => Interlocked.Increment(ref opened);
    player.MediaFailed += (_,_) => Interlocked.Increment(ref failed);
    player.Open(new Uri(file), autoplay:false, positionSeconds:1.5);
    await Wait(() => Volatile.Read(ref opened)>0, "paused-open event");
    await Task.Delay(250);
    Check(!player.IsPlaying, "paused open stays paused");
    player.IsMuted = true;
    Check(player.IsMuted && player.Volume == 0, "mute does not overwrite configured volume");
    player.IsMuted = false;
    Check(!player.IsMuted, "unmute is restored through the session contract");
    Check(!player.WantsPlayback && player.Position.TotalSeconds >= 1.4, "paused handoff preserves intent and projected starting clock");
    player.Play();
    await Wait(() => player.IsPlaying && player.Position.TotalSeconds>1.6, "resume advances from requested start");
    Check(player.Position.TotalSeconds<3, "start position is not skipped");
    var information = (player as IPlaybackAudioInformation)?.AudioInformation;
    Check(information is { SampleRateHz: 44100, Channels: 1 }, "decoder audio information passes through the optional host contract");
    player.Pause();
    await Wait(() => !player.IsPlaying, "pause acknowledged");
    var paused=player.Position.TotalSeconds; await Task.Delay(350);
    Check(Math.Abs(player.Position.TotalSeconds-paused)<.15, "pause freezes clock");
    player.SpeedRatio=1.25;
    player.Position=TimeSpan.FromSeconds(3);
    player.Play();
    await Wait(() => player.Position.TotalSeconds>=3 && player.Position.TotalSeconds<4.5, "seek/resume");
    player.Close();
    var eventBaseline=Volatile.Read(ref opened);
    player.Open(new Uri(file), autoplay:false, positionSeconds:2);
    await Wait(() => Volatile.Read(ref opened)>eventBaseline, "reopen event");
    Check(!player.IsPlaying, "reopen retains paused intent");
    Check(!player.WantsPlayback && player.Position.TotalSeconds >= 1.9, "replacement does not reset paused position to zero");
    var profile=player.CapturePlaybackProfile();
    Check(profile.Volume==0 && profile.SpeedRatio==1.25, "volume/rate retained across media replacement");
    Check(Volatile.Read(ref failed)==0, "no decoder error");
    var ended=0;
    player.MediaEnded += (_,_)=>Interlocked.Increment(ref ended);
    for(var cycle=0;cycle<2;cycle++)
    {
        player.Position=TimeSpan.FromSeconds(7.5); player.Play();
        await Wait(()=>Volatile.Read(ref ended)>cycle,"natural end event");
        var beforeRestart=Volatile.Read(ref opened);
        player.Restart();
        await Wait(()=>Volatile.Read(ref opened)>beforeRestart && player.IsPlaying && player.Position.TotalSeconds>.1,"repeat reopens ended decoder");
        Check(player.Position.TotalSeconds<2,"repeat starts at zero instead of the ended position");
    }
    Console.WriteLine("PASS natural EndReached -> Restart -> playback from zero, two consecutive cycles.");
    Console.WriteLine("PASS real LibVLC: paused open, start position, resume, pause, seek, replacement and shared profile; silent synthetic WAV.");
    player.Close();
    var videoFile=Path.Combine(folder,"synthetic.avi");
    SyntheticVideo.Write(videoFile);
    // Access only this test-owned player; do not add a diagnostic escape hatch to production.
    var raw=(MediaPlayer)typeof(BundledAudioPlayer).GetField("_player",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(factory.Session)!;
    var engine=(LibVLC)typeof(BundledAudioPlayer).GetField("_libVlc",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(factory.Session)!;
    Check(LibVlcAudioOutputConfiguration.ResolveModule(engine, AudioOutputSettings.Default) == "directsound",
        "automatic playback keeps the startup-compatible output (no switch back to mmdevice)");
    try
    {
        player.Open(new Uri(videoFile), autoplay:true, positionSeconds:1, video:true, audioSlave:new Uri(file));
        await Wait(()=>player.ReadVideoFrame() is {Length:>100} && raw.AudioTrackCount>1,"video frames and slave audio track");
        using (var image = System.Drawing.Image.FromStream(new MemoryStream(player.ReadVideoFrame()!)))
            Check(image.Width == 160 && image.Height == 90, "production frame is decodable and preserves aspect ratio");
        Check(raw.AudioTrack>=0,"slave audio selected");
        player.Pause(); await Wait(()=>!player.IsPlaying,"video pause");
        var position=player.Position.TotalSeconds; await Task.Delay(300);
        Check(Math.Abs(player.Position.TotalSeconds-position)<.15,"video pause freezes timeline");
        player.Position=TimeSpan.FromSeconds(3); player.Play();
        await Wait(()=>player.Position.TotalSeconds>=3 && player.Position.TotalSeconds<5,"video seek");
        Check(Volatile.Read(ref failed)==0,"video/slave no decoder error");
        player.Close();
        Check(player.ReadVideoFrame() is null,"closing clears the previous video picture");
        player.Open(new Uri(videoFile), autoplay:false, positionSeconds:2.5, video:true, audioSlave:new Uri(file));
        await Wait(()=>raw.State==VLCState.Paused,"paused video opens without audible playback");
        Check(!player.IsPlaying && !player.WantsPlayback,"paused video preserves intent");
        player.Play();
        await Wait(()=>player.ReadVideoFrame() is {Length:>100},"resuming paused video produces a complete picture");
        for (var cycle = 0; cycle < 3; cycle++)
        {
            // Same session and the production exit contract. No UI-controlled HWND/output reset.
            player.Close();
            Check(player.ReadVideoFrame() is null, "video exit clears old picture");
            player.Open(new Uri(file), autoplay: true, positionSeconds: 1);
            await Wait(() => player.IsPlaying && player.Position.TotalSeconds > 1.2, "audio after video");
            Check(player.ReadVideoFrame() is null, "audio cannot expose previous video");
            player.Close();
            player.Open(new Uri(videoFile), autoplay: true, positionSeconds: 1, video: true, audioSlave: new Uri(file));
            await Wait(() => player.ReadVideoFrame() is { Length: > 100 } && player.Position.TotalSeconds > 1.2,
                "same session video after audio has a fresh picture");
            Check(player.CapturePlaybackProfile().Volume == 0 && player.SpeedRatio == 1.25, "handoff retains output profile");
        }
        Check(Volatile.Read(ref failed) == 0, "repeated handoff has no decoder error");
        Console.WriteLine("PASS three video/audio/video cycles through IPlaybackSession with fresh frames, retained profile and no visible native output window.");
        Console.WriteLine("PASS real LibVLC: generated AVI decoded to memory, WAV slave selected, pause and seek. This is not HWND/compositor or acoustic A/V-sync verification.");
    }
    finally { player.Dispose(); }
    if (transport is not null)
    {
        await prepared!.DisposeAsync();
        await transport.DisposeAsync();
        Check(!File.Exists(file), "transport source released after real decoder disposal");
        Console.WriteLine("PASS independent transport -> real decoder -> disposal: cached WAV used for audio and video slave; no network/account/UI.");
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL media engine: {e.GetType().Name}: {e.Message}");
    return 1;
}
finally
{
    // Only this invocation's uniquely created synthetic fixture directory.
    Directory.Delete(folder, recursive:true);
}
static void Check(bool condition, string label) { if(!condition) throw new InvalidOperationException(label); }
static async Task Wait(Func<bool> condition, string label)
{
    var timer=Stopwatch.StartNew();
    while(!condition()) { NativeWindowGuard.AssertNoOwnedVisibleWindow(); if(timer.Elapsed>TimeSpan.FromSeconds(8)) throw new TimeoutException(label); await Task.Delay(25); }
    NativeWindowGuard.AssertNoOwnedVisibleWindow();
}
