# Isolated MV window contract fixture

This WPF executable links the production MusicVideoWindow XAML/code and injects a synthetic IPlaybackSession.
It does not instantiate a real decoder, contact a platform, read profiles or initialize the Auralis application.

Run dotnet run --project tools/PlaybackWindowTests -c Release -- --run <new-output-directory>.
The unit/interaction checks invoke the production handlers on offscreen WPF objects, and write 18 PNGs across
light/dark, 720/1040/1920-DIP widths and 100/150/200% rendering scales. Rendering scale is not physical DPI.
Assertions cover loading versus first frame, duplicate/malformed images, controls, volume/mute synchronization,
seek, repeat, errors and disposal. They are not actual keyboard, maximize/fullscreen, media or performance tests.

Launching this fixture executable with no arguments (or --show) displays only this fake-session window,
with a fixed geometric sample image and no network/audio. It is intended for separately approved Computer Use.
Do not report actual window acceptance based on the offscreen tests.
