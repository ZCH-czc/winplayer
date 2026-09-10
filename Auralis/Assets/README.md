# Application icon provenance

The PNG and multi-size ICO were replaced on 2026-09-09 at the product owner's request.
The new mark was generated specifically for Auralis using the built-in OpenAI image generation
tool, without an input/reference image. It does not reuse the previous icon's bitmap.
The user requested a replacement aligned with the application's Fluent visual language.

Design: a free-standing, blue-to-teal folded sound-wave silhouette suggesting an A, with broad
rounded strokes and restrained depth. No enclosing glass tile, text, play triangle or external logo.
Generation prompt:

> Use case: logo-brand. Create ONE final Windows desktop app icon for Auralis, a local-first music player with a restrained Fluent UI. Original new design, no reference image. Transparent background with real alpha, square canvas, icon fills about 84% of canvas. Subject: an elegant abstract sound-wave mark made of three broad softly rounded ribbon strokes, the central stroke tallest, composed into a compact balanced silhouette subtly suggesting the letter A. Cool blue and muted teal with gentle tonal gradients. Modern Microsoft Fluent-inspired clarity, very light layered depth but mostly clean graphic geometry. No enclosing rounded-square app tile; let the sound-wave symbol itself be the silhouette. Broad shapes and clear spacing readable at 16/24/32 pixels. Front facing and optically centered. No text, no letters printed, no musical-note cliché, no play triangle, no headphones, no circular disc, no tiny lines, no glass bubble, no chrome bevels, no heavy glow, no strong cast shadow, no background scenery, no mockup or contact sheet. Professional quiet desktop icon, not a glossy mobile-game badge. Do not copy any existing product logo.

Packaging preserves the generated alpha rather than applying the legacy square-tile mask.
`AuralisIcon.png` is the maintained 1024×1024 raster asset, with transparent margins;
`Auralis.ico` contains 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixel frames derived from it.
The existing MSIX asset pipeline consumes this same PNG. No third-party font is bundled in this mark.

This records how the asset was made; it is not a claim of trademark clearance, exclusive rights,
or a substitute for the project's final public-distribution review.
