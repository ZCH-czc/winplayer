using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Auralis.Services;

/// <summary>
/// Audio-first playback backed by bundled LibVLC, with opt-in composited video frames.
/// Unlike WPF MediaPlayer, this does not rely on optional Windows Media Foundation codecs.
/// </summary>
internal sealed class BundledAudioPlayer : IPlaybackSession, IPlaybackAudioInformation
{
    private PlaybackAudioInformation? _audioInformation;
    private bool _audioInformationRead;
    public PlaybackAudioInformation? AudioInformation
    {
        get
        {
            if (_audioInformationRead || _media is null || _player.Length <= 0) return _audioInformation;
            _audioInformationRead = true;
            try
            {
                var audioSource = _audioSlave ?? (!_video ? _source : null);
                if (audioSource?.IsFile == true)
                {
                    using var file = new FileStream(audioSource.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    _audioInformation = FlacAudioInformation.Read(file);
                    if (_audioInformation is not null) return _audioInformation;
                }
                var track = _media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Audio);
                if (track.TrackType != TrackType.Audio) return null;
                var codec = new string(Enumerable.Range(0, 4).Select(i => (char)((track.Codec >> (i * 8)) & 255)).ToArray()).Trim();
                if (codec.Any(c => c < 32 || c > 126)) codec = "";
                _audioInformation = new(codec, track.Bitrate > 0 ? (int)Math.Min(int.MaxValue, track.Bitrate / 1000L) : null,
                    (int)track.Data.Audio.Rate, null, (int)track.Data.Audio.Channels,
                    codec.Equals("flac", StringComparison.OrdinalIgnoreCase), false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            { /* Missing optional measurements must never interrupt playback. */ }
            return _audioInformation;
        }
    }
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly SynchronizationContext? _eventContext;
    private Media? _media;
    private int _opening;
    private int _mediaGeneration;
    private double _volume = 0.75;
    private double _speedRatio = 1;
    private AudioOutputSettings _audioOutputSettings = AudioOutputSettings.Default;
    private AudioOutputSettings? _appliedAudioOutputSettings;
    private bool _disposed;
    private Uri? _source;
    private Uri? _audioSlave;
    private bool _video;
    private bool _ended;
    private long _initialPositionMilliseconds;
    private bool _hasCurrentClock;
    private CompositedVideoFrames? _videoFrames;
    public byte[]? ReadVideoFrame() => _videoFrames?.ReadLatest();
    public bool WantsPlayback { get; private set; }

    public BundledAudioPlayer(SynchronizationContext? eventContext = null)
    {
        _eventContext = eventContext ?? SynchronizationContext.Current;
        VerifyNativeRuntime();
        Core.Initialize(NativeDirectory());
        _libVlc = new LibVLC("--intf=dummy", "--quiet", LibVlcAudioOutputConfiguration.StartupOutputOption);
        try
        {
            _player = new MediaPlayer(_libVlc);
            _player.EnableHardwareDecoding = true;
            _player.EnableMouseInput = false;
            _player.EnableKeyInput = false;

            _player.Playing += OnPlaying;
            _player.Paused += OnPlaying;
            _player.EndReached += OnEndReached;
            _player.EncounteredError += OnEncounteredError;
            _player.Volume = ToVlcVolume(_volume);
            _player.TimeChanged += OnTimeChanged;
        }
        catch
        {
            // A failed factory cannot hand ownership to the host. Release partially created native
            // resources before the registry tries a fallback. Native process crashes remain uncatchable.
            try { _player?.Dispose(); }
            finally { _libVlc.Dispose(); }
            throw;
        }
    }

    public event EventHandler? MediaOpened;

    public event EventHandler? MediaEnded;

    public event EventHandler<AudioPlaybackFailedEventArgs>? MediaFailed;

    public TimeSpan Position
    {
        get
        {
            var time = Math.Max(0, _player.Time);
            // start-paused may not publish a clock until the first decoded frame. Do not turn
            // an audio/video handoff at 01:30 into a synthetic 00:00 seek during that interval.
            return TimeSpan.FromMilliseconds(Volatile.Read(ref _hasCurrentClock) ? time : _initialPositionMilliseconds);
        }
        set
        {
            _initialPositionMilliseconds = Math.Max(0, (long)value.TotalMilliseconds);
            Volatile.Write(ref _hasCurrentClock, false);
            _player.Time = Math.Max(0, (long)value.TotalMilliseconds);
        }
    }

    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(0, _player.Length));

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            _player.Volume = ToVlcVolume(_volume);
        }
    }

    public double SpeedRatio
    {
        get => _speedRatio;
        set
        {
            _speedRatio = Math.Clamp(value, 0.5, 2);
            _ = _player.SetRate((float)_speedRatio);
        }
    }

    public bool IsPlaying => _player.IsPlaying;

    public bool IsMuted { get => _player.Mute; set => _player.Mute = value; }

    public AudioPlaybackProfile CapturePlaybackProfile() => new(
        _audioOutputSettings with { },
        _volume,
        _speedRatio);

    public void Open(Uri source, bool autoplay = true, double positionSeconds = 0, bool video = false, Uri? audioSlave = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        // Stop the old decoder before changing callback/output ownership. No UI HWND is involved.
        _player.Stop();
        _videoFrames?.Clear();
        CloseCurrentMedia();
        if (video)
        {
            _videoFrames ??= new CompositedVideoFrames();
            _videoFrames.Attach(_player);
        }
        Interlocked.Increment(ref _mediaGeneration);
        _source = source;
        _audioSlave = audioSlave;
        _video = video;
        _ended = false;
        WantsPlayback = autoplay;
        _initialPositionMilliseconds = double.IsFinite(positionSeconds) ? (long)(Math.Clamp(positionSeconds, 0, 864000) * 1000) : 0;
        Volatile.Write(ref _hasCurrentClock, false);
        ApplyAudioOutputSelection();
        var media = new Media(_libVlc, source);
        media.AddOption(video ? ":video" : ":no-video");
        if (video && audioSlave is not null) media.AddOption($":input-slave={audioSlave.AbsoluteUri}");
        foreach (var option in AudioPlaybackStartOptions.Create(
                     _audioOutputSettings.BufferMilliseconds, _speedRatio, autoplay, positionSeconds))
            media.AddOption(option);
        _media = media;
        _player.Media = media;
        Interlocked.Exchange(ref _opening, 1);

        // Configure before decoding starts. Do not seek, change rate or call Play again from
        // Playing: that disturbs the startup clock and can consume audio before the UI is ready.
        _player.Volume = ToVlcVolume(_volume);
        if (!_player.Play())
        {
            Interlocked.Exchange(ref _opening, 0);
            Raise(() => MediaFailed?.Invoke(this, new AudioPlaybackFailedEventArgs("无法启动随应用提供的音频解码器。")));
        }
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_player.Media is null)
        {
            return;
        }

        WantsPlayback = true;

        if (_ended) Restart();
        else _ = _player.Play();
    }

    public void Restart()
    {
        if (_source is { } source) Open(source, autoplay: true, video: _video, audioSlave: _audioSlave);
    }

    public void Pause()
    {
        WantsPlayback = false;
        if (!_disposed && _player.CanPause)
        {
            _player.SetPause(true);
        }
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Exchange(ref _opening, 0);
        Interlocked.Increment(ref _mediaGeneration);
        _player.Stop();
        _videoFrames?.Clear();
        CloseCurrentMedia();
        _source = null;
        _audioSlave = null;
        _ended = false;
        WantsPlayback = false;
        _initialPositionMilliseconds = 0;
        Volatile.Write(ref _hasCurrentClock, false);
    }

    public void ApplyAudioOutputSettings(AudioOutputSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);

        _audioOutputSettings = LibVlcAudioOutputConfiguration.Normalize(_libVlc, settings);

        // Device and channel selection can be changed while a media item is active. The output
        // module itself is applied on the next Open(), as required by LibVLC.
        LibVlcAudioOutputConfiguration.ApplyDeviceAndChannel(_libVlc, _player, _audioOutputSettings);
    }

    public AudioDeviceSnapshot GetAudioDeviceSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var modules = LibVlcAudioOutputConfiguration.GetModules(_libVlc);
        var devices = new List<AudioOutputDeviceInfo>();
        foreach (var module in modules)
        {
            try
            {
                foreach (var device in _libVlc.AudioOutputDevices(module.Id) ?? [])
                {
                    if (string.IsNullOrWhiteSpace(device.DeviceIdentifier))
                    {
                        continue;
                    }

                    devices.Add(new AudioOutputDeviceInfo(
                        module.Id,
                        device.DeviceIdentifier,
                        string.IsNullOrWhiteSpace(device.Description)
                            ? device.DeviceIdentifier
                            : device.Description));
                }
            }
            catch
            {
                // Not every LibVLC output module supports enumerating devices. The module can
                // still be selected and use the current Windows default endpoint.
            }
        }

        var distinctDevices = devices
            .DistinctBy(device => $"{device.ModuleId}\u001f{device.Id}", StringComparer.Ordinal)
            .ToArray();
        var activeDeviceId = _player.OutputDevice ?? string.Empty;

        return new AudioDeviceSnapshot(
            modules,
            distinctDevices,
            _audioOutputSettings,
            activeDeviceId,
            "Windows 音频引擎协商",
            "保持音源采样率；设备不支持时由 Windows 转换",
            "PCM；当前播放后端不声明 DSD 直通或独占位完美输出",
            AudioEndpointLatencyInfo.Unavailable("notRequested"));
    }

    public AudioEndpointProbeRequest? GetAudioEndpointProbeRequest(AudioDeviceSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);

        var resolvedModule = LibVlcAudioOutputConfiguration.ResolveModule(_libVlc, _audioOutputSettings);
        if (resolvedModule?.Equals("mmdevice", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        var configuredDeviceId = _audioOutputSettings.OutputDeviceId;
        var activeMmDevice = snapshot.Devices.FirstOrDefault(device =>
            device.ModuleId.Equals("mmdevice", StringComparison.OrdinalIgnoreCase) &&
            device.Id.Equals(snapshot.ActiveDeviceId, StringComparison.Ordinal));
        var probeDeviceId = !string.IsNullOrWhiteSpace(configuredDeviceId)
            ? configuredDeviceId
            : activeMmDevice?.Id;
        var fallbackName = snapshot.Devices.FirstOrDefault(device =>
            device.ModuleId.Equals("mmdevice", StringComparison.OrdinalIgnoreCase) &&
            device.Id.Equals(probeDeviceId, StringComparison.Ordinal))?.DisplayName;
        return new AudioEndpointProbeRequest(probeDeviceId, fallbackName);
    }

    private void ApplyAudioOutputSelection()
    {
        // Keep the existing output alive across tracks. Re-selecting the same module forces
        // unnecessary device teardown/reinitialization, especially noticeable on Bluetooth.
        if (_appliedAudioOutputSettings == _audioOutputSettings) return;
        _audioOutputSettings = LibVlcAudioOutputConfiguration.ApplySelection(
            _libVlc,
            _player,
            _audioOutputSettings);
        _appliedAudioOutputSettings = _audioOutputSettings;
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        var generation = Volatile.Read(ref _mediaGeneration);
        if (Interlocked.Exchange(ref _opening, 0) == 1)
        {
            Raise(() =>
            {
                if (!_disposed && generation == Volatile.Read(ref _mediaGeneration))
                {
                    MediaOpened?.Invoke(this, EventArgs.Empty);
                }
            });
        }
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (e.Time >= 0) Volatile.Write(ref _hasCurrentClock, true);
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        Raise(() => { _ended = true; WantsPlayback = false; MediaEnded?.Invoke(this, EventArgs.Empty); });
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _opening, 0);
        Raise(() => MediaFailed?.Invoke(this, new AudioPlaybackFailedEventArgs(
            "随应用提供的解码器无法读取该音频；文件可能不完整或格式与扩展名不一致。")));
    }

    private void CloseCurrentMedia()
    {
        _audioInformation = null;
        _audioInformationRead = false;
        _player.Media = null;
        _media?.Dispose();
        _media = null;
    }

    private static int ToVlcVolume(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 100);

    internal static void VerifyNativeRuntime()
    {
        var nativeDirectory = NativeDirectory();
        var requiredFiles = new[]
        {
            Path.Combine(nativeDirectory, "libvlc.dll"),
            Path.Combine(nativeDirectory, "libvlccore.dll"),
            Path.Combine(nativeDirectory, "plugins", "codec", "libflac_plugin.dll")
        };
        var missing = requiredFiles.Where(path => !File.Exists(path)).Select(Path.GetFileName).ToArray();
        if (missing.Length == 0) return;
        throw new FileNotFoundException(
            $"Auralis 音频解码组件不完整（缺少 {string.Join("、", missing)}）。" +
            "请重新下载发行压缩包，并将其中所有文件完整解压后再运行；不要只复制 Auralis.exe。",
            requiredFiles.First(path => !File.Exists(path)));
    }

    private static string NativeDirectory()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException(
                $"Auralis 暂不支持 {RuntimeInformation.ProcessArchitecture} 架构。")
        };
        var componentDirectory = Path.GetDirectoryName(typeof(BundledAudioPlayer).Assembly.Location);
        if (string.IsNullOrEmpty(componentDirectory)) throw new PlatformNotSupportedException("Playback requires a file-based component deployment.");
        return Path.Combine(componentDirectory, "libvlc", architecture);
    }

    private void Raise(Action action)
    {
        var generation = Volatile.Read(ref _mediaGeneration);
        void Dispatch()
        {
            if (!_disposed && generation == Volatile.Read(ref _mediaGeneration)) action();
        }
        if (_eventContext is null)
        {
            ThreadPool.QueueUserWorkItem(_ => Dispatch());
            return;
        }

        _eventContext.Post(static state => ((Action)state!).Invoke(), (Action)Dispatch);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();
        _player.Playing -= OnPlaying;
        _player.TimeChanged -= OnTimeChanged;
        _player.Paused -= OnPlaying;
        _player.EndReached -= OnEndReached;
        _player.EncounteredError -= OnEncounteredError;
        _player.Dispose();
        _videoFrames?.Dispose();
        _libVlc.Dispose();
        _disposed = true;
    }
}
