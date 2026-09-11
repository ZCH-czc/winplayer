# Auralis promotional page

English-first static promotional page with a Chinese language switch and automatic,
UI scene presentation. Both languages use a silent visual demonstration.
`index.html`, `styles.css`, `app.js`, `player/` and `assets/` form
a standalone site; asset paths work under a repository subdirectory.

It uses the Auralis Fluent-inspired fonts, blue accent, 6 px control corners and 8 px
surfaces. CSS translucency is not native Mica. Animations support a pause control,
system reduced motion, hidden-tab suspension and offscreen suspension.

English screenshots and the icon use the public repository's original demonstration assets.
Chinese gallery images include the owner-authorized 大海带不走 album artwork and supplied LRC.
See [screenshot provenance](../screenshots/README.md) and
[icon provenance](../../Auralis/Assets/README.md). Screenshots show demonstration
content, not real audio output or measured sound quality. No audio autoplays.

Six automatic scenes show dark/light records, dark/light immersive artwork, the queue
and lyric controls. Only the dark/light pair uses live public Web UI and original
22-second record rotation. Other scenes use a fixed-ratio screenshot carousel;
the live clock stops while hidden. `player/provenance.json` records source hashes;
`player/LICENSE` contains its MIT license.

The presentation frame uses an in-memory preference store, a no-op native bridge and a
CSP blocking connections and media. It does not decode or play audio. Its six scene
buttons, language control and motion control remain outside the non-interactive frame.
These source resources are an explicit allowlist, not a copy of application binaries,
user state or optional private packages. Web translucency does not demonstrate native Mica.

The customization section's five accent swatches select matching settings screenshots
using the real blue, teal, violet, coral or amber tokens. There is no live settings
iframe. Keyboard navigation and checked state are supported; language changes preserve
the selection. Images load before crossfading; stale loads cannot override newer choices.
`assets/gallery/README.md` documents the 22 bilingual captures and their content sources.

No provider implementation, private repository link, account, analytics or external
runtime dependency is included. The page links to the public source and user guide,
and explicitly identifies the current distribution as a source preview.

The page and expanded-player backgrounds include gentle motion with pause and reduced-motion
paths. English uses original silent fixtures. Chinese uses 大海带不走 by 泠鸢yousa,
with public promotional permission confirmed by the owner on 2026-09-11. See
[media provenance](assets/dahai/README.md); third-party media is not MIT-licensed.

The standalone Chinese song card, including its play, seek and volume controls, has
been removed. The page does not load or play audio. The authorized media assets remain
preserved, and the Chinese full-screen demonstration keeps its album artwork and LRC
with a silent presentation clock. Record rotation, theme transitions and screenshot
carousels remain; motion pauses when hidden or reduced motion is requested.

## Publishing

The owner-approved GitHub Pages site is built from this directory by
`.github/workflows/pages.yml`. Changes to this directory on `main` validate and publish
the website; the workflow can also be run manually. It uploads only `docs/site`, not the
application source tree, packages, private plugins or local state. Repository visibility
is not changed by this workflow. Run `node tools/Test-PromotionalSite.cjs` from the
repository root before updating the page.
