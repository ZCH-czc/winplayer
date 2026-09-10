# Playback component contract (experimental 0.3)

IPlaybackSession is the backend-only audio/video boundary. It carries no LibVLC, WPF, WebView, HWND,
platform plugin or credential-store types. Methods are serialized by the owner. The implementation
dispatches lifecycle events onto the synchronization context selected when creating a session.

The host still owns queues, platform leases and authorized source transport, UI state and timeout policy.
MediaOpened indicates decoder state, not that a complete video frame exists. ReadVideoFrame returns the
latest immutable JPEG or null; Close and media replacement clear the previous picture. Video output
configuration belongs to Open(video: true), not a window-handle operation exposed to the UI.

Bundled default implementation: ../Auralis.Playback.LibVlc. It preserves existing local-first behavior and
is always distributed with the player. Playback.Host owns explicit factory registration, compatibility,
disk discovery, user-approved component import/selection and pre-creation fallback. The default decoder
does not require an online platform plugin. These contracts are versioned, not an unrestricted stable ABI.

The historical Auralis.Services namespace keeps current serialization and source migration small.
Audio device diagnostic DTOs are transitional; Windows probing remains in the application.

0.2 adds IsMuted independently of normalized Volume, allowing the native MV controls to preserve the
selected volume when muting. Both primary playback and the legacy MV window now consume this contract.

0.3 adds IPlaybackComponentFactory, immutable component descriptors and capability flags. Each activation
owns a fresh factory; each creation returns a fresh stopped session. VerifyRuntime must not start playback
or open an audio device. Factories must clean up partially allocated resources if creation throws.

IPlaybackAudioInformation is an optional additive interface: existing IPlaybackSession implementations
remain valid without implementing it. Observations describe the currently opened media, not a provider's
requested quality. Missing values are unknown. The bundled FLAC reader reports sample rate, bit depth
and channels from STREAMINFO, and approximate average encoded bitrate excluding metadata blocks; this
is not instantaneous bitrate or uncompressed PCM bandwidth. Other media use available decoder metadata.
Close/replacement clears observations; the UI also checks current track ID.
