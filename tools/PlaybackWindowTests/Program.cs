using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Auralis;
using Auralis.Platform.Abstractions;
using Auralis.Services;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var fake = new FixtureSession();
        var lease = new PlatformVideoLease(new Uri("https://example.invalid/synthetic"), DateTimeOffset.UtcNow.AddMinutes(10), "video/mp4", "MP4");
        var window = new MusicVideoWindow(lease, "组件验收 · 合成视频", "Auralis 离线夹具", true,
            new(AudioOutputSettings.Default, .37, 1.25), fake);
        window.Title = "Auralis MV component fixture — no network";
        if (args.Length == 0 || args.Contains("--show"))
        {
            fake.Frame = CreateFrame();
            window.Closed += (_, _) => application.Shutdown();
            application.Run(window);
            return 0;
        }
        if (args.Length != 2 || args[0] != "--run")
        {
            window.Close();
            Console.WriteLine("Use --run <new-render-directory> for offline tests, or --show for an isolated fake-session UI.");
            return 2;
        }
        try
        {
            using var factory = new LibVlcPlaybackFactory();
            Check(PlaybackServices.Bundled.Descriptor == factory.Descriptor,
                "application registration matches bundled factory without activation");
            if (Environment.GetEnvironmentVariable("AURALIS_PLAYBACK_PACKAGE") is { Length: > 0 } packagePath)
            {
                var package = Auralis.Playback.Host.PlaybackComponentCatalog.Discover(packagePath,
                    PlaybackCapabilities.CompletePlayer).Single();
                Check(package.Issue == Auralis.Playback.Host.PlaybackPackageIssue.None &&
                    package.Package!.Manifest.Descriptor == factory.Descriptor &&
                    package.Package.Manifest.EntryType == typeof(LibVlcPlaybackFactory).FullName,
                    "explicit build candidate matches actual default factory metadata/type");
            }
            var output = Path.GetFullPath(args[1]);
            if (Directory.Exists(output)) throw new InvalidOperationException("Choose a new render directory.");
            Directory.CreateDirectory(output);
            Call(window, "OnLoaded", window, new RoutedEventArgs());
            Check(fake.OpenCount == 1 && fake.Video && fake.Volume == .37 && fake.SpeedRatio == 1.25, "open/profile contract");
            Check(Element<Border>(window, "StatusOverlay").Visibility == Visibility.Visible, "opened is not first frame");
            fake.Frame = CreateFrame();
            Call(window, "UpdateFrame");
            Check(Element<Image>(window, "VideoHost").Source is BitmapImage && Element<Border>(window, "StatusOverlay").Visibility == Visibility.Collapsed, "first complete frame");
            var first = Element<Image>(window, "VideoHost").Source;
            Call(window, "UpdateFrame");
            Check(ReferenceEquals(first, Element<Image>(window, "VideoHost").Source), "unchanged frame not decoded twice");
            fake.Frame = [1, 2, 3];
            Call(window, "UpdateFrame");
            Check(ReferenceEquals(first, Element<Image>(window, "VideoHost").Source), "malformed frame keeps previous image");
            fake.Frame = CreateFrame();
            Call(window, "UpdateFrame");
            Click(window, "PlayPauseButton");
            Check(!fake.WantsPlayback && (string)Element<Button>(window, "PlayPauseButton").ToolTip == "播放", "pause and icon");
            Click(window, "PlayPauseButton");
            Check(fake.WantsPlayback, "resume");
            Click(window, "MuteButton");
            Check(fake.IsMuted && fake.Volume == .37, "mute preserves volume");
            Element<Slider>(window, "VolumeSlider").Value = 62;
            Check(!fake.IsMuted && fake.Volume == .62, "volume unmutes");
            window.ApplyPlaybackRate(1.5);
            Check(fake.SpeedRatio == 1.5, "shared rate");
            window.ApplyPlaybackVolume(.23);
            Check(fake.Volume == .23 && Element<Slider>(window, "VolumeSlider").Value == 23, "shared volume");
            Call(window, "UpdateProgress");
            Element<Slider>(window, "ProgressSlider").Value = 42000;
            Call(window, "ProgressSlider_PreviewMouseUp", window, null!);
            Check(fake.Position == TimeSpan.FromSeconds(42), "seek uses milliseconds");
            fake.End();
            Check((string)Element<Button>(window, "PlayPauseButton").ToolTip == "重新播放", "end state");
            Click(window, "PlayPauseButton");
            Check(fake.WantsPlayback && fake.Position == TimeSpan.Zero, "replay");
            Check(Element<Slider>(window, "ProgressSlider").Value == 0 && Element<TextBlock>(window, "PositionText").Text == "0:00", "replay updates progress immediately");
            foreach (var dark in new[] { false, true })
            foreach (var scale in new[] { 1d, 1.5d, 2d })
            foreach (var size in new[] { new Size(720, 480), new Size(1040, 650), new Size(1920, 1080) })
            {
                window.ApplyTheme(dark);
                var root = Element<Grid>(window, "Root");
                root.BeginAnimation(UIElement.OpacityProperty, null);
                root.Opacity = 1;
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
                var view = Element<Image>(window, "VideoHost");
                Check(view.ActualWidth > 0 && view.ActualHeight > 0 && view.Stretch == Stretch.Uniform, "video geometry");
                var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"{dark}-{scale}-{size.Width}.png"));
                encoder.Save(stream);
            }
            fake.Fail();
            Check(Element<Border>(window, "StatusOverlay").Visibility == Visibility.Visible, "failure overlay");
            window.Close();
            Check(fake.Disposed, "window owns/disposes session");
            fake.End();
            Console.WriteLine($"PASS MV window: {_checks} assertions, 18 offscreen theme/size/render-scale images; fake session, no account/network, not physical DPI or acoustic verification.");
            return 0;
        }
        catch (Exception e)
        {
            window.Close();
            Console.Error.WriteLine($"FAIL MV fixture: {e}");
            return 1;
        }
    }
    private static T Element<T>(MusicVideoWindow window, string name) => (T)window.FindName(name);
    private static void Click(MusicVideoWindow window, string name) => Element<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Call(MusicVideoWindow window, string method, params object[] args) =>
        typeof(MusicVideoWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, args);
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); _checks++; }
    private static byte[] CreateFrame()
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.Teal, null, new Rect(0, 0, 640, 360));
            draw.DrawRectangle(Brushes.Goldenrod, null, new Rect(320, 0, 320, 360));
            draw.DrawEllipse(Brushes.WhiteSmoke, null, new Point(320, 180), 80, 80);
        }
        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}

internal sealed class FixtureSession : IPlaybackSession
{
    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<AudioPlaybackFailedEventArgs>? MediaFailed;
    public TimeSpan Position { get; set; }
    public TimeSpan Duration => TimeSpan.FromMinutes(3);
    public double Volume { get; set; }
    public bool IsMuted { get; set; }
    public double SpeedRatio { get; set; }
    public bool IsPlaying => WantsPlayback;
    public bool WantsPlayback { get; private set; }
    public bool Disposed { get; private set; }
    public bool Video { get; private set; }
    public int OpenCount { get; private set; }
    public byte[]? Frame { get; set; }
    private AudioOutputSettings _settings = AudioOutputSettings.Default;
    private bool _ended;
    public void Open(Uri source, bool autoplay = true, double positionSeconds = 0, bool video = false, Uri? audioSlave = null)
    {
        OpenCount++; Video = video; WantsPlayback = autoplay; Position = TimeSpan.FromSeconds(positionSeconds);
        MediaOpened?.Invoke(this, EventArgs.Empty);
    }
    public void Play() { if (_ended) Restart(); else WantsPlayback = true; }
    public void Pause() => WantsPlayback = false;
    public void Restart() { _ended = false; Position = TimeSpan.Zero; WantsPlayback = true; MediaOpened?.Invoke(this, EventArgs.Empty); }
    public void Close() { WantsPlayback = false; Frame = null; }
    public AudioPlaybackProfile CapturePlaybackProfile() => new(_settings, Volume, SpeedRatio);
    public void ApplyAudioOutputSettings(AudioOutputSettings settings) => _settings = settings;
    public AudioDeviceSnapshot GetAudioDeviceSnapshot() => new([], [], _settings, "", "", "", "", AudioEndpointLatencyInfo.Unavailable("fixture"));
    public AudioEndpointProbeRequest? GetAudioEndpointProbeRequest(AudioDeviceSnapshot snapshot) => null;
    public byte[]? ReadVideoFrame() => Frame;
    public void Dispose() { Disposed = true; Close(); }
    public void End() { _ended = true; WantsPlayback = false; MediaEnded?.Invoke(this, EventArgs.Empty); }
    public void Fail() => MediaFailed?.Invoke(this, new("fixture"));
}
