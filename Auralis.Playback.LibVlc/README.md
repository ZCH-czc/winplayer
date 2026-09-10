# Bundled LibVLC playback component

Owns the main playback engine, decoder start options, output selection and bounded memory video frames.
References only Playback.Abstractions plus decoder packages, not Auralis or online providers.
LibVlcPlaybackFactory is the registered composition entry; windows hold IPlaybackSession. The factory
delegates to PlaybackEngine and is stateless; each session owns its native resources.

Open stops the previous decoder, clears old frames and binds memory callbacks before starting video.
There is no SetVideoWindow/HWND API on this session. Audio/video transitions must not reset LibVLC to
native Direct3D output. Close clears the frame; the callback buffer lives until decoder disposal.

MusicVideoWindow now consumes IPlaybackSession and renders memory frames with a WPF Image. It no longer
uses native decoder types or a video HWND. The main application's direct LibVLC package references and
friend-assembly access have been removed. Only the factory composition point references the implementation.
Playback.Host now handles explicit registration, compatibility, selection and pre-creation fallback.
Trusted disk activation now exists in Playback.Host; user import/selection and host-owned media transport remain work.
Experimental implementation 0.4.0 resolves libvlc relative to its own assembly location and passes that
directory to Core.Initialize. The bundled layout is unchanged, while separate component packages no
longer accidentally use the executable's native directory. Single-file deployment is unsupported.
Managed constructor failures dispose partially created native resources before the registry can fall back.

Both current consumers use the bounded 1280-edge/~30-FPS JPEG memory path. This removes the old MV
window's direct native/GPU surface, so high-resolution rendering cost and acoustic synchronization still
need real-device acceptance; synthetic correctness checks do not prove that performance equivalence.

tools/MediaEngineSmoke references this actual assembly (not linked player source), generates silent
WAV/AVI fixtures and checks clock, pause, seek, repeat, independent audio track and three consecutive
video/audio/video cycles. It observes this test process's visible top-level windows without modifying them.
No real accounts, user media, acoustic sync, full WebView UI or device/DPI matrix are covered.
