namespace Auralis.Platform.Abstractions;

/// <summary>Optional chapter and timed-comment metadata; never contains playback URLs.</summary>
public interface IPlatformMediaExtrasCapability
{
    /// <summary>Returns playable parts belonging to an entity.</summary>
    Task<PlatformResult<IReadOnlyList<PlatformTrack>>> GetPartsAsync(PlatformEntityId id, CancellationToken cancellationToken);
    /// <summary>Returns a bounded timed-comment snapshot for the selected part.</summary>
    Task<PlatformResult<IReadOnlyList<PlatformTimedComment>>> GetDanmakuAsync(PlatformEntityId id, CancellationToken cancellationToken);
}

/// <summary>Plain, non-scripted timed text.</summary>
public sealed record PlatformTimedComment(double Seconds, string Text);
