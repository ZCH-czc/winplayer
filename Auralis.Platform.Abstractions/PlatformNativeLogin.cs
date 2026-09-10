namespace Auralis.Platform.Abstractions;

/// <summary>Optional native sign-in supplied by a trusted desktop plugin. Never sent to WebView.</summary>
public interface IPlatformNativeLoginCapability
{
    /// <summary>Prepares an isolated login session without creating a window on a worker thread.</summary>
    Task<PlatformResult<IPlatformNativeLoginSession>> CreateLoginSessionAsync(CancellationToken cancellationToken);

    /// <summary>Clears only this provider's isolated profile after explicit sign-out.</summary>
    Task<PlatformResult<PlatformUnit>> ClearLoginDataAsync(nint ownerWindow, CancellationToken cancellationToken);
}

/// <summary>
/// Host-owned lifetime for a trusted plugin window. Show/Activate/Close are called on the desktop UI
/// thread. Authentication may take arbitrarily long; it is not a timed router operation. No secrets
/// cross this interface. The host closes sessions before unloading the plugin host.
/// </summary>
public interface IPlatformNativeLoginSession
{
    /// <summary>Raised only after provider-side account verification and secure persistence succeed.</summary>
    event EventHandler? Authenticated;
    /// <summary>Raised after the isolated surface closes, including cancellation.</summary>
    event EventHandler? Closed;
    /// <summary>Shows the isolated window owned by an existing native window.</summary>
    void Show(nint ownerWindow, bool dark);
    /// <summary>Activates the already open login surface.</summary>
    void Activate();
    /// <summary>Cancels pending login work and closes the surface. Must be idempotent.</summary>
    void Close();
}

/// <summary>Restores a saved online reference without interpreting its ID in the local player.</summary>
public interface ITrackDetailsCapability
{
    /// <summary>Returns metadata for the exact entity; a title hint must never select another song.</summary>
    Task<PlatformResult<PlatformTrack>> GetTrackAsync(
        PlatformEntityId id, string? titleHint, CancellationToken cancellationToken);
}
