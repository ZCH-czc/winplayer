# Original declarative-page sample

Current stage 7: sample v2 declares media cards (Pages v6 / Host SDK 2.10) and a synthetic StreamResolution capability.
The frozen-host upgrade check follows query → catalogue → media handle → existing lease resolver, with no HTTP/decoder.
Settings revision revokes old page media. All new page, settings, media content and routes reside only in the plugin.
The sample URI is intentionally example.test, not an audio file. This is not an audible playback or installed-update test.
Earlier stage records below describe previous verification and SDK floors.

Stage 6 adds a typed link from global query results to the media catalogue and its continuation.
The sample declares Pages v5 / SDK 2.9; the host resolves the new entity context without a fabricated track.
The current upgrade test freezes 15 SDK 2.9 runtime files; only the independently built plugin changes.
Historical stage 5 details below still describe the grouped-setting behavior, not the current SDK floor.

Stage 5 also adds grouped bilingual settings in v2 and a choice-dependent ordering preference.
The plugin consumes the effective preference through IPlatformSettings and reverses its sample record cards;
switching back to default ordering makes the preference unavailable and restores the original order.
The upgrade test freezes 15 Host SDK 2.8 runtime files, then replaces only independently built sample packages.
It verifies settings projection, persistence, dependency changes and actual plugin output along with Pages v4.
No account, HTTP, installed app or native playback is tested. New packages require the 2.8 foundational host.

This offline sample demonstrates a plugin-only upgrade against one frozen Host SDK 2.8 runtime.
No platform code, user account, music or HTTP request is used.

- Revision 1 declares only a legacy profile page.
- Revision 2 adds discography cards, incremental paging, a discussion/reply chain, a global sidebar
  entry and Overview / Notes / Archive tabs.
- All new routes, labels and content live in the plugin DLL and inert manifest.
- Global pages use a separate request type with no fabricated track or creator.

From the repository root run:

```powershell
pwsh -NoProfile -File tools/Test-PluginPageUpgrade.ps1
```

The script builds both revisions separately, approves only these generated test packages in isolated
artifact directories, routes them through the production host/coordinator, and checks hashes of every
frozen host runtime file. It never installs into the user's profile. Generated images are demonstration
URLs, not downloaded media; artwork still requires an explicit allowed domain in an actual plugin.

This proves expansion inside the implemented vocabulary, not arbitrary future UI or installed-MSIX
upgrades. An SDK 2.2 installation first needs the new host. See docs/PLUGIN_PAGES.md for the contract.
# Stage 4 extension

The expanded v2 sample now declares Pages v4 / Host SDK 2.7 and adds a text query plus an explicit
collection choice in its global hub. The original v1 manifest still exposes only its original profile.
The upgrade runner freezes the same host runtime for both real DLLs and asserts that query/filter
results are routed without changing those files. This is original offline demo content, not a platform API.
