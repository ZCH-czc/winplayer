# Auralis user guide

[Home](../README.md) · [简体中文](USER_GUIDE.zh-CN.md)

## Organize your music

Use **Add music** on the Songs page, or manage music folders in Settings. Browse by song, album, or artist, and use search to find a track.

Favorites keep frequently played tracks close by. Recently played helps you return to something you just heard, while My playlists lets you organize your collection your way.

![Local library demonstration](screenshots/library-en-US.png)

*Track names, artwork, and quality values are documentation samples.*

## Open the player

Click the current-track area in the bottom bar to expand the player. Play/pause, previous/next, seeking, volume, and playback modes remain available. The right-side queue opens within the player.

In the full-screen player settings, choose the cover-focused layout or record-and-lyrics layout. One emphasizes artwork; the other places lyrics beside the record. The in-app player and Windows full-screen mode are separate states; F11 controls window full-screen mode.

![Dark record-and-lyrics demonstration](screenshots/player-en-US.png)

*This is a paused UI demonstration, not an animation or audio-output test.*

## Adjust appearance and lyrics

- Choose light, dark, or system appearance.
- Adjust layout, background, cover transitions, and lyric presentation in the player settings.
- Check the visual preview before returning to playback. The preview does not play audio.
- Use reduced motion if you prefer fewer animated effects.
- Timestamped LRC files can follow playback. Line-level highlighting is not word-level timing.
- Configure desktop lyrics when you want a separate floating lyric display.

![Player settings preview](screenshots/customize-en-US.png)

*The preview reflects the same layout and theme preferences, helping you compare choices before returning to the player.*

## Read audio information

The bottom bar and expanded player share current quality information. Available bitrate appears on the label; hover for further format details.

For FLAC, “≈” denotes average encoded bitrate. Sample rate is shown in kHz, bit depth in bit, and channel count in ch. Some media or playback components cannot provide every field. Missing information does not imply reduced quality.

## Manage extension components

The default playback backend is bundled. Normal playback requires no additional component installation.

If you need a replacement, open the plugin management page in Settings, expand Advanced components, and use the dedicated playback-component import control. Import only trusted, compatible packages. Review the name, version, and capabilities, explicitly confirm trust, then enable and restart. Restarting stops current playback.

Package types are not interchangeable: playback components use dedicated `.auralis-playback.zip` packages; other package types have their own entry points. Plugins run inside the player process, not a security sandbox. Integrity checks are not proof of a trusted publisher. There is currently no graphical uninstall or rollback control.

## Shortcuts and troubleshooting

- Use Tab / Shift+Tab to move focus, and Enter / Space to operate focused buttons.
- Review or customize playback shortcuts in Settings. Windows media keys can also control playback.
- If lyrics are missing, check the local LRC/TXT file or select a file from the current track's lyric options.
- If there is no sound, check system and application volume, then the output device.
- Unsupported Mica configurations fall back to a solid surface without affecting basic playback.
- Whether closing the window exits the application depends on tray settings. Use the tray exit command when a full shutdown is needed.

All images render actual application resources in an isolated browser, using original demonstration data. They do not access a personal library, load extension packages, or produce audio. They are not native-window captures or evidence of playback performance or sound quality. See [screenshot notes](screenshots/README.md).
