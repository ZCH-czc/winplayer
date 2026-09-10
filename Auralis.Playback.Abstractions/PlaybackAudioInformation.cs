namespace Auralis.Services;

/// <summary>Optional decoder observation; no source URI, headers or platform identity.</summary>
public interface IPlaybackAudioInformation
{
    PlaybackAudioInformation? AudioInformation { get; }
}

/// <summary>Encoded average/nominal bitrate, never the decoded PCM throughput.</summary>
public sealed record PlaybackAudioInformation(string Codec, int? BitrateKbps, int? SampleRateHz,
    int? BitsPerSample, int? Channels, bool IsLossless, bool IsAverageBitrate);
