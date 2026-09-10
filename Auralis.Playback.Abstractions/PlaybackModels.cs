namespace Auralis.Services;

public sealed record AudioOutputSettings(
    string OutputModule,
    string OutputDeviceId,
    string Channel,
    int BufferMilliseconds)
{
    public static AudioOutputSettings Default { get; } = new("auto", string.Empty, "stereo", 1000);
}

public sealed record AudioPlaybackProfile(
    AudioOutputSettings OutputSettings,
    double Volume,
    double SpeedRatio);

public sealed record AudioOutputModuleInfo(string Id, string DisplayName);

public sealed record AudioOutputDeviceInfo(string ModuleId, string Id, string DisplayName);

public sealed record AudioEndpointProbeRequest(string? EndpointId, string? FallbackName);

public sealed record AudioDeviceSnapshot(
    IReadOnlyList<AudioOutputModuleInfo> Modules,
    IReadOnlyList<AudioOutputDeviceInfo> Devices,
    AudioOutputSettings Settings,
    string ActiveDeviceId,
    string OutputFormat,
    string SampleRateBehavior,
    string DsdBehavior,
    AudioEndpointLatencyInfo EndpointLatency);

public sealed class AudioPlaybackFailedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed record AudioEndpointLatencyInfo(
    string EndpointId,
    string DeviceName,
    bool IsBluetooth,
    double? EstimatedLatencyMilliseconds,
    double? StreamLatencyMilliseconds,
    double? EnginePeriodMilliseconds,
    string Status)
{
    public static AudioEndpointLatencyInfo Unavailable(string status) =>
        new(string.Empty, string.Empty, false, null, null, null, status);
}
