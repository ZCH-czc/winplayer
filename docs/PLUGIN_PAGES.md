# Declarative plugin pages — through stage 9

## Stage 9: independent musician discovery, still without a core update

QQ plugin **1.16.0** adds a `discover` global main-page entry (document v5), CreatorSearch and GlobalPages.
It reuses the existing v6 creator entry for profiles, songs, albums and album contents. Host SDK **2.10.0**,
Abstractions **1.11.0**, app development version **0.16.11**, core production sources and published Web assets stay unchanged.
The sidebar name, bilingual form, 10/20 result choices, search interpretation and continuation are plugin-owned.
Native continues to own opaque handles, typed targets, artwork policy, history, revision invalidation and media playback intent.

Queries accept at most 128 characters, reject controls and unexpected fields, and submit only on explicit user intent.
Continuations bind the normalized query hash and requested size; server-clamped effective page size is retained rather than
skipping results. Identity, current page, next page and response envelope are checked before projecting any result.
No credential access, speculative audio, endpoint fallback or account writes occur during discovery or catalogue browsing.
Only successful empty responses become “No matching musicians”; upstream errors remain typed failures with retry UI.

Actual public catalogue verification found a song with **100 artist credits**. Rejecting the complete catalogue for this was
incorrect. The plugin now validates a bounded upstream list (at most 200), preserves order/identities and projects the first
32 credits into the existing bounded media contract. This is a display-metadata summary, not a change to track identity or rights.

Evidence (local artifacts are not committed or installed):

- Final development packages: `artifacts/qq-discovery-stage9-verified-20260913`.
- `artifacts/qq-discovery-stage9-evidence/upgrade-result.json`: **649 source/published files unchanged**, published SDK copies
  byte-identical, **17 fixed probe runtime files**, actual QQ 1.15/1.16 DLL comparison. The probe links unmodified Native source;
  this is not an installed-player or hot-swap acceptance test.
- `probe-result.json`: **89 checks** — inert discovery, plugin-only new entries, both search paths, exact typed creator navigation,
  works/albums/media, clamped paging, wrong/foreign/oversized input, cancellation, stale responses, errors, empty/retry behavior,
  collaboration normalization and revocation. HTTP and accounts are synthetic; page fixtures are projected by the actual coordinator.
- `private-platforms/DiscoveryProbe/Test-Ui.cjs`: **6 cases** using the frozen published Web UI: light/dark, browser scale 1/1.5/2,
  narrow/wide, populated/empty library, reduced motion, keyboard/IME, automatic append, profile/catalogue/back, failure/retry,
  normal creator search and stale/missing-plugin behavior. Short plugin navigation labels avoid sidebar clipping.
  Empty prefetch handles are core cancellation messages, not plugin media fetches; nonempty browse prefetch is forbidden.
- Shared query/global/media page suites: **18 cases passed**. Solution Release build: 0 warnings/errors; core and Host suites pass;
  provider regressions **50/50**, actual split plugins **54/54**, contract checker **89**, JavaScript logic **27/27**;
  published core-only boundary and whitespace checks pass.

### Live results must not be reported as uniformly passing

During this stage an earlier 1.16 candidate completed five anonymous metadata calls for `泠鸢yousa`: nine search results,
one profile, 20 songs, 20 albums and 15 album songs. This also exposed and verified the collaboration fix.
The **final package recheck failed** on search with `ServiceUnavailable`; a wider query intended to check real pagination
also stopped on its first request. `live-result.json` and `live-pagination-result.json` intentionally retain **Passed:false**.
Search had also returned upstream code 2001 in an initial diagnostic; its meaning is not guessed as login expiry or empty results.
No automatic endpoint/account fallback was attempted. Live search stability and successful live pagination remain unverified.
Read-only diagnostics use isolated services that deny credential access/writes, allow only named metadata RPCs, cap calls,
disable cookies/redirects and stop on failure. No audio, installed application or Windows native DPI acceptance occurred.

Reproduce the local fixed-host proof from the repository root:

```powershell
pwsh -NoProfile -File private-platforms/DiscoveryProbe/Test-Upgrade.ps1 -PreviousPlatforms artifacts/qq-catalog-stage8-20260913/platforms -UpdatedPlatforms artifacts/qq-discovery-stage9-verified-20260913/platforms -EvidenceDirectory artifacts/qq-discovery-stage9-evidence
node private-platforms/DiscoveryProbe/Test-Ui.cjs
```

The baseline is the stage-eight preexisting frozen core inventory, checked before/after running both actual plugins.
No signed MSIX, user state, current test entry, Git push or Release was changed.
Next: standardize these independent-plugin delivery checks, retaining separate static/offline/live/native statuses;
never let a green synthetic result override a failed live service check.

## Stage 8: a real catalogue update with the host frozen

No production core, Host SDK, Abstractions, playback component or Web resource change was required.
Host SDK stays **2.10.0**, Abstractions **1.11.0**, app development version **0.16.11**.
The private QQ provider moves from 1.14 to 1.15 and declares its existing creator entry as a v6 main page.
It owns About / Songs / Albums tabs and album-song navigation, stable card identities and bounded continuations.
The host still owns presentation, opaque handles, page history, artwork proxying, keyboard behavior and playback intent.

Songs use normalized media cards, preserving artwork, exact artist/album references, duration, availability and MV metadata.
Browse and queue operations do not grant playback rights. Explicit Play uses the original resolver and account/quality checks;
page browsing never reads credentials or eagerly resolves the catalogue's audio. The existing next-track scheduler is unchanged.
Album dates remain date-only text, not invented UTC publication instants. Pagination advances by actual returned rows (including
duplicates), so a server-clamped page does not skip items. Malformed identities/envelopes/cursors fail with typed errors.

Evidence in the private development workspace:

- `artifacts/qq-catalog-stage8-evidence/upgrade-result.json`: **649 core source/published files unchanged**;
  frozen SDK assemblies byte-identical to the stage-seven publish, **17 unchanged probe runtime files**,
  distinct actual QQ 1.14/1.15 DLLs. The headless probe links identical Native coordinator sources;
  this is not an in-place upgrade test of an installed EXE.
- `probe-result.json`: **59 checks**, including inert discovery, main-page manifest behavior, native projection,
  wrapped song parsing, duplicate/stable media identities, continuation context, invalid requests, restricted responses,
  retry/empty states, creator context, explicit rights-aware resolution and revision revocation.
- `live-result.json`: actual plugin, published SDK, **five anonymous metadata requests**: profile, two song pages
  (20 + 20 cards), 20 albums and 10 songs from one album. No account or audio lease was used.
- `private-platforms/CatalogueProbe/Test-Ui.cjs`: native-projected original fixtures in the **unchanged published Web UI**;
  light/dark × browser scale 1/1.5/2, wide/narrow, empty/populated local state, reduced motion, keyboard,
  append, error/retry, album/back navigation, queue/play acknowledgement and revision/missing-plugin behavior.
  Six cases passed; SVG artwork and transport acknowledgements are synthetic. This is not real Windows DPI or audible playback.
- Release build: 0 warnings/errors; core + Host suites passed, provider regressions 50/50, split DLL regressions 54/54,
  checker 89 assertions, JavaScript logic 27/27; core-only published boundary and whitespace checks passed.
- The existing media-page suite also passed all six frozen-publish cases, including full-screen next-track metadata,
  empty queue startup, pending replacement, preview/unavailable handling and actual-next prefetch planning (synthetic Native bridge).

No signed MSIX, installation, current-entry replacement, push or release was performed. Existing SDK 2.2 installations still need
a compatible base host before importing SDK 2.10 plugins. The independent plugin-update promise applies to compatible contracts,
not arbitrary new system privileges or unrestricted scripts.

Next: retain SDK 2.10 and add a provider-owned global discovery / musician search flow using existing GlobalPages, Query and
CreatorSearch contracts. Record any genuine contract gap rather than hiding new provider rules in the player.

## Stage 7: media cards use the existing player

Development **Host SDK 2.10.0 / Abstractions 1.11.0**. Platform API 1, manifest schema 5 and assembly identities remain unchanged.
Pages v6 adds optional `PlatformPageCard.Media: PlatformTrack`; older documents cannot carry it.
Entries returning v6 require documentVersion:6, minimumHostSdkVersion:2.10.0 and declarative-pages.v1 through v6.
The provider must also declare StreamResolution. No URL, header, token or executable action is added to the page protocol.

The host validates the entire document before issuing handles: same provider (including artist, album and video references),
bounded IDs/text/artists/duration, known availability and HTTPS artwork. Page media artwork uses the manifest domain policy,
including redirects; denied images have no cover URL and cannot bypass the policy through the track handle.
Raw entity IDs and remote artwork remain native-side. The Web sees the existing safe OnlineTrackView in card.media.

Reading a page or its continuation only registers metadata. It does not obtain a lease, touch the local library,
change the queue or start prefetch. Host-owned Play and Add to queue controls are separate from read-only navigation actions:

- Play uses the existing pending-playback/commit path. Resolution failure preserves current playback and the page.
- Add to queue appends to a mixed queue, keeps local entries/current clock, deduplicates the same active media handle,
  and does not autoplay. If a replacement is already pending, the appended item also survives its eventual commit.
- An empty player's transport button starts the queued item instead of implicitly selecting the local library.
- Availability/preview rights, lyrics, video, quality, auto-advance and fullscreen metadata remain on the existing playback path.
- Only the actual planned successor is prefetched after foreground playback has advanced; no whole-page downloads.
- Queue additions are capped at 1,000 items through this entry. Existing local-library contents are not truncated.

Page media handles share the bounded 1,600-track registry and two-hour lifetime. Repeated reads reuse an active page handle
for the same entity. Plugin/account/settings revisions revoke these references; a late lease is discarded rather than used.
The shared prefetch context also checks handle validity. Reopen the page for new handles after a revision change.
A UI enqueue alone is not a fresh rights check: Native revalidates the handle/provider and resolves rights when it is played/prepared.

Bilibili development package **1.18.0** maps only definite major.archive video posts into normalized media cards.
Pure text/image posts and forward-only content do not receive fabricated playback controls. Creator/my-space entries declare v6;
the discovery form retains v5. Platform parsing and API/rights logic remain in the private plugin.
The original offline sample v2 adds media cards and a synthetic lease capability on an unchanged SDK 2.10 host.
Its example.test lease is not downloaded or decoded. Existing v1–v5 contracts remain supported.

Verification (2026-09-13):
- 37 new Native media checks plus existing Host tests: inert reading, opaque projection, provider/artist/video ownership,
  capability gate, artwork/redirect boundaries, preview/unavailable behavior, stable identity and late-revision rejection.
- Core services passed; provider regression 50/50; independently packaged plugin regression 54/54.
- SDK contract checker: 89 assertions. JavaScript logic: 27/27 including mixed/empty/pending queue selection.
- Frozen-host proof: `artifacts/page-upgrade-68fca92cf7f94a63910c8771bb55858e/result.json`.
  All 15 host test runtime files were unchanged between independently built sample v1/v2;
  MediaCardsUseExistingPlayback / TypedEntityNavigation / SettingsConsumedByPlugin are true.
- Final Release solution: zero warnings/errors. Core-only published output:
  `artifacts/plugin-media-core-stage7-20260913`, passed Test-PluginBoundary.
- Published mediapages/querypages/globalpages/pages/pagefeeds/community suites passed all 36 theme/scale cases.
  Existing media suite initially lost its browser session after four cases; standalone rerun passed all six cases.
  Thus all seven suites have passing coverage, not an uninterrupted seven-suite run.
  Media-page checks include mixed/empty queue, failed playback, exact-next prefetch, auto-advance/fullscreen metadata,
  restored/maximized/narrow layouts and reduced motion. Light-wide and dark-narrow screenshots inspected.
- Six independent packages / 11 providers inspected inertly in `artifacts/plugin-media-stage7-20260913`;
  Bilibili package 1.18.0 SHA-256: `5EDDE2638171A399542A69ABDB48D16139AFB030DE01D803F951E90B304BCB08`.
- No signing, MSIX installation, current-entry replacement, push or release was performed. `git diff --check` passed.
- UI uses isolated Edge + synthetic bridge, not personal accounts, audible playback or installed-MSIX upgrades.
- This foundational SDK still needs a future app release before an old installed SDK can understand v6.
  Once installed, new pages/catalogues/settings using this vocabulary can ship in plugin updates alone.
  New native permissions, unknown control types, host security fixes and safe active-DLL hot replacement remain separate work.

## Historical stage 6

## Stage 6: typed, read-only entity navigation

Host SDK **2.9.0 / Abstractions 1.10.0**, API 1, schema 5, stable assembly identities.
Pages v5 adds optional `PlatformPageAction.Target = PlatformPageTarget(EntryId, Entity, ContextKind)`.
The existing action constructor remains compatible. The source entry declares documentVersion:5,
minimumHostSdkVersion:2.9.0 and declarative-pages.v1/v2/v3/v4/v5. Global sources also declare global-pages.v1.
Older SDKs fail closed before activation; raising a manifest cannot add implementation to an old DLL.

Only page-level/card actions may transfer an entity. Next, tabs and query submits cannot: automatic
pagination or pressing a filter must not silently change entity context. A transfer requires:
- Same exact provider ownership; a bounded nonempty entity ID (at most 512 units, no control characters).
- A declared target entry with presentation:page; the source must also be a main page.
- ContextKind:creator to a creator entry accepting creator context, or media to a media entry.
- The provider's existing Pages capability. Global, dialog, undeclared and executable targets are denied.

The action's Route/State belong to the target; they remain backend-only. The target entry can retain an older
documentVersion if its own output needs no v5 fields. Target document validation uses that entry's version,
not the source entry's version. The original Web owner/entry remains the envelope for the entire browsing
history, while each opaque navigation handle holds its own effective entry/entity/kind. Descendant tabs,
actions, forms and continuations inherit that effective context automatically. Explicit transfers clear
source query values; ordinary navigation keeps its existing query snapshot. No playable track, lease or
native playback command is created, including for ContextKind:media.

The Web action still contains **only label and random handle**. It cannot supply or inspect Target/Entity,
override the source owner, switch providers or promote a read to an account write. Whole-document validation
precedes artwork authorization and link registration. Revision changes revoke target actions, continuations
and discussion references; cancellation/late-result checks apply before rendering.

Existing Fluent rendering and bounded history are reused. Failed navigation preserves the source page;
Back/Escape restores the committed query/scroll and originating button focus when its position/text still
match, falling back to the heading. No CSS or native-command mechanism is added. As before, back re-reads the
previous document; it does not restore all appended feed batches. Context handles expire after 30 minutes,
with 512 native references and 64 Web history entries.

Bilibili's Discover → creator → About/Activity → discussion path now uses the typed link, eliminating the
plugin's discover-target JSON wrapping/remapping. Its personal-space implementation still resolves its own
account identity. The SDK sample additionally verifies global search → media catalogue → incremental
records without forging a media-search handle. No new account-write or automatic playback behavior is added.

Stage 6 verification (2026-09-13):
- Native target suite: 42 checks through the actual collectible fixture DLL; cross-provider/entry/type,
  opaque projection, exact large ID, query isolation, nested media/creator contexts, tabs/feed/comments,
  cancellation and revision rejection. Manifest tests cover v5 feature and SDK floor.
- Frozen-host proof: `artifacts/page-upgrade-a515e82ea3c3496f90ec8769b05426b9/result.json`.
  Both independent sample DLLs run on the same 15 unchanged host test runtime files. V2 adds a typed
  target and its continuation, in addition to grouped settings and earlier page features.
- Source query UI suite: light/dark × 1/1.5/2, including narrow, reduced motion, empty library,
  missing/revoked plugins, failed target retry, keyboard Back/Escape and originating-link focus.
- These checks use synthetic data, not real Bilibili accounts, audible playback, installed-MSIX upgrade
  or native multi-monitor DPI. The signed 0.16.11/SDK 2.2 app is not updated by these development outputs.

Final development verification:
- Release solution: zero warnings/errors; core service tests and Host fixtures passed.
- Provider regression: 50/50; actual split-DLL regression: 54/54 against
  `artifacts/plugin-navigation-stage6-20260913/platforms`. Six packages / 11 providers passed inert inspection.
- SDK contract checker: 89 assertions; JavaScript logic: 23/23.
- Core-only publish `artifacts/plugin-navigation-core-stage6-20260913` passed the boundary guard.
  Published plugin-pages.js/index.html match source; Host/Abstractions match the tested binaries.
- Published querypages/globalpages/pages/pagefeeds/community/media: 36 light/dark × scale cases passed.
  Light-wide and dark-narrow screenshots inspected in `artifacts/plugin-query-pages-ui`.
- `git diff --check` passed. No signing, installation, current-entry replacement, push or release performed.

Development: **Host SDK 2.9.0**, **Abstractions 1.10.0**, API 1, manifest schema 5.
This is not an update to the already signed 0.16.11 / SDK 2.2 package.
Install the new foundational host before using these plugins. Afterwards, changes within this
document vocabulary can ship only in plugins, through explicit import/trust/enable/restart.

## Stage 4: explicit read-only query and filters

Pages v4 adds optional `Query: PlatformPageQuery(Submit, Fields)`. Entries that can return it
declare documentVersion:4 and declarative-pages.v1/v2/v3/v4; global entries additionally declare
GlobalPages/global-pages.v1. SDK 2.6 rejects the new package safely. All previous page constructors,
API/enum values and assembly identities remain unchanged; old Pages v1/v2/v3 remain supported.

A document has at most one form with 1–4 fields and unique bounded ASCII keys. Each field has public
plain-text Label, Placeholder, Value, MinLength and MaxLength. Text fields are single-line, at most
256 UTF-16 units, with no control characters; an empty initial value can be shown even when the
submitted minimum is nonzero. Choice fields declare 1–16 unique nonempty values (at most 64 units)
and labels; the initial/submitted value must be one of them. Choices use MinLength=0.
Field/choice labels and submit label are at most 100 units; placeholder at most 200. No file,
password, URL-command, arbitrary HTML, custom scripts or account-write fields exist in this vocabulary.
Public option values must not contain credentials, signed URLs or platform entity identities.

The host renders Fluent text inputs, accessible native radio semantics with themed choice cards,
and an accent submit button. Typing/selecting never fetches; Enter outside IME composition or an explicit
submit button does. Editing cancels an in-flight query and suspends auto-pagination of the previous
results. Failure keeps the old results and editable draft; retry replays the submitted snapshot.
Success creates a new collection and keeps text focus. Back navigation restores the prior committed
query, not an unrelated current draft. Query history is bounded in-memory (64 entries), never localStorage,
library data or logs, and expires with the native navigation handles. Closing/revocation cancels work.

The public projection contains only `query.submit.{label,handle}` and safe field definitions.
The native navigation registry owns the actual submit route/state, validated immutable schema and query
snapshot. Both read request records add InputValues without changing their constructors. Explicit input
is accepted only with this form's opaque handle: exact keys/count, string types, lengths and choice
membership are checked before plugin invocation. Ordinary navigation/Next must omit inputs and inherit
the immutable snapshot; it cannot replace criteria or cross provider/entry/revision/context boundaries.
An appended response cannot carry Query or Tabs. Form submits count toward the 128-action page budget.

This is a read contract, not general form submission authority. A trusted in-process DLL is still not a
sandbox. Account writes, new control kinds, settings sections and generalized entity transfers are not
implemented by this stage and require a separately reviewed host contract.

### Stage 4 verification (2026-09-13)

- Release solution: zero warnings/errors; core services, Host routing/compatibility and 89 SDK checker
  assertions pass. New native cases cover invalid schemas/inputs, no plugin call on rejection, cross-context
  handles, snapshot mutation, exact criteria on append, and revocation.
- Frozen host / two real plugin DLLs: `tools/Test-PluginPageUpgrade.ps1` now exercises a new form and
  choice filter in plugin v2 as well as global pages, tabs, feeds, discussions and nested replies.
  Evidence: `artifacts/page-upgrade-5dae1c2783114ec9b0d0f28429c59580/result.json`;
  all 15 frozen test-host runtime files unchanged. This is not a claim that an older SDK 2.2 binary
  already contains the newly introduced renderer.
- Independent private package build/static checks are separate from the core-only publish output:
  `artifacts/plugin-pages-stage4-20260913` and `artifacts/plugin-pages-core-stage4-20260913`.
  No installation, MSIX signing, real account changes or GitHub publication is performed.
- `npm run test:ui -- querypages globalpages pages pagefeeds community media` tests actual core resources
  passes 36 scenarios in isolated Edge using synthetic native messages; query tests include IME, choice semantics, inert input,
  back/retry, reversed responses, missing/revoked plugins and plain-text injection checks. Light/dark,
  1×/1.5×/2× pixel ratios, reduced motion and narrow layouts are covered. Additional field-name clobbering
  checks ensure plugin keys cannot shadow form methods or object prototypes. Screenshots live in
  `artifacts/plugin-query-pages-ui`. These checks do not replace live provider availability, real audio,
  native-window/fullscreen or cross-monitor DPI acceptance.

## Stage 3: global navigation and tabs (retained)

An enabled, configured provider may declare up to two global entries, within the existing eight-entry
budget. It must declare both Pages and GlobalPages and implement IPlatformGlobalPagesCapability.
Global entry metadata requires placement:global, presentation:page and documentVersion:3.
Required host features are global-pages.v1 and declarative-pages.v1/v2/v3, plus the ordinary schema-5
artwork declaration. SDK 2.5 and earlier reject the package instead of hiding an unsupported feature.

The host projects these entries into an optional sidebar section. Without a suitable plugin the entire
section is absent. Discovery and sidebar rendering never call ReadGlobalPageAsync. Only clicking an entry
reads the plugin's page; opening it neither selects music nor invokes a login command. The section has a
bounded scroller; the Settings button and playback bar remain reachable at short heights.

The separate PlatformGlobalPageReadRequest contains ProviderId, Route, State and Language, with no fake
media/entity ID. Existing media/creator requests and their constructors are unchanged. Global navigation
handles are bound to the provider, entry and revision and cannot cross into a media/creator context.

Pages v3 adds Tabs: an empty list or two to eight PlatformPageTab(Action, Selected) entries.
Exactly one is selected; duplicate route/state pairs, nulls and invalid actions are rejected. Actions are
still bounded read-only navigation. Tabs count toward the 128-action document budget. A regular v3
media/creator page can use tabs without declaring a global entry or the GlobalPages capability.

An incremental response must omit Tabs. It appends within the existing collection and cannot silently
change sections. Tab selection commits only after success; failure preserves the current content and
selection. A new tab click cancels the pending read, including an append. Late responses cannot switch
back to the older tab. Arrow/Home/End keys move focus, Enter/Space activate; the selected tab receives
focus after success. Only the content panel has a short 160ms transition, skipped under reduced motion.
Tab changes replace the current history slot rather than filling Back with filter changes.

Additional bridge message:

- readPluginGlobalPage {providerId,entryId,navigationHandle?,language,requestId}
- setPluginPage {providerId,handle:null,entryId,requestId,page?,error?}
- page.tabs is [{action:{label,handle},selected}]; no source route, state or raw entity ID.

Cancellation, 30/35-second budgets, proxy artwork, comments, limits and revocation rules are shared with
the existing page coordinator and renderer. A global entry is not permission to run arbitrary native
commands, inject scripts, submit account writes or alter playback.

The private complex-feed implementation now declares a personal-space entry and activity/about tabs.
It resolves the authenticated user's identity inside the plugin; without a session it returns an
AuthenticationRequired explanation directing the user to Settings. It does not open login automatically.
This is the user's own profile/activity, not a claimed following-feed or complete social client.

## Ownership

Plugins own entry labels, route names, content, layout selection, gallery grouping, attribution,
comment subjects and read-only continuation state. The host owns Fluent rendering, main navigation,
keyboard/focus, reduced motion, limits, artwork proxy, cancellation and authorization boundaries.
There is no platform-specific route branch in the generic handler or renderer.

The new complex-feed plugin uses this contract for both a work's author and a precise CreatorSearch
result. The legacy CreatorFeed surface remains for older plugins; it is not the new route's content owner.
Comments and nested replies reuse Comments / CommentReplies through a typed discussion handle.
A page action cannot post a comment, like, follow, play music or execute an arbitrary native command.

The trusted DLL runs in process, **not in a sandbox**. Declarative rendering accepts no HTML, scripts,
styles, WPF objects or arbitrary execution names; this does not make malicious DLL code safe.

## Entry declaration and compatibility

A v2 creator entry, within a complete schema-5 manifest:

```json
{
  "hostRequirements": {
    "minimumHostSdkVersion": "2.5.0",
    "requiredFeatures": ["comment-artwork.v1", "declarative-pages.v1", "declarative-pages.v2"]
  },
  "providers": [{
    "id": "sample.pages",
    "displayName": "Page sample",
    "capabilities": ["Pages"],
    "commentArtworkDomains": [],
    "pages": [{
      "id": "profile",
      "label": "资料",
      "labelEn": "Profile",
      "placement": "creator",
      "presentation": "page",
      "documentVersion": 2,
      "acceptsCreatorContext": true
    }]
  }]
}
```

This is an excerpt, not an installable package. Declare every additionally implemented capability and
its required feature. See the complete original sample under plugin-sdk/samples/DeclarativePages.

- At most eight entries per provider, with at most one creator placement.
- IDs are bounded ASCII identifiers, not URLs or file paths.
- Defaults: documentVersion 1, presentation dialog, acceptsCreatorContext false.
- page presentation / creator context require documentVersion at least 2; version 2 requires declarative-pages.v2.
- Version 3 also accepts these placements and requires declarative-pages.v3; global entries use the additional rules above.
- The returned document version cannot exceed the entry's declared version.
- Metadata discovery validates these declarations without loading assemblies or accessing the network.
- Existing Pages v1 and legacy community capabilities remain compatible; no enum ordinals or constructors changed.

## Document vocabulary

| Field | Version | Meaning |
| --- | --- | --- |
| Title, Description, Layout | 1+ | Plain display text; cards or list |
| Cards: Title, Text, Image, PublishedAt, Actions | 1+ | Bounded text, optional single public image/date, read-only navigation |
| Image | 2 | Introductory profile artwork |
| Cards: Id | 2 | Required stable provider-local key; never sent raw to Web |
| Cards: Author, Avatar, Images | 2 | Attribution and up to nine public gallery images |
| Cards: Discussion, CommentCount | 2 | Same-provider subject for the existing comments/reply drawer |
| Next | 2 | Read-only continuation appended to this collection |
| Tabs | 3 | Plugin-owned section labels, selected state and read-only actions |

Regular Actions replace the document and add a back-navigation entry. Next is separately marked as
append by Native; Web cannot turn an arbitrary read into an append operation. Header/introduction stays
in place during append. Stable host card handles deduplicate repeated/pinned cards without rebuilding
already read DOM. Comments preserve dates, avatars, emotes, sorting and reply paging already supported
by that provider; inaccessible, deleted or folded content is not claimed to have been recovered.

Images pass the explicit provider artwork policy and only host proxy URLs reach Web. The gallery
supports previous/next, arrow keys, Escape, image-error text and focus return. The main page shares
the application content scroller, sidebar and transport; a dialog has its own body scroller.
Returning to search preserves the query/results/scroll and visible originating control.

## Context, projection and lifecycle

The existing ReadPageAsync request constructor remains unchanged. Its MediaId property is the
host-resolved context entity; the additive ContextKind says media (legacy default) or creator.
Only a declared creator-context entry may consume a creator handle. A creator never becomes a track.
Route/state remain backend-only and must not contain credentials.

Bridge messages remain:
- readPluginPage {handle,entryId,navigationHandle?,language,requestId}
- setPluginPage {handle,entryId,requestId,page?,error?}
- cancelPlatformExtras {kind:"plugin-page"}

The safe page projection adds image, append, collectionHandle, next. Cards add handle, author, avatar,
images, discussionHandle, commentCount. Actions still contain only label and random handle.
No raw entity IDs, provider cursors, route state, cookies or media leases go to the main WebView.

Navigation is bound to the initial handle, entry and account/configuration revision. Native compares
backend continuation route/state to reject stalled or cyclic cursors, rather than comparing freshly
randomized Web handles. Revisions revoke page reads, discussion links and creator-search contexts.
A native 30-second budget / Web 35-second watchdog, explicit cancellation and response identity guards
prevent an old request from replacing current content. Errors preserve content and stop automatic retry.

## Limits

- Per response: 100 cards, 128,000 total main text characters; v1 body 16,000 / v2 body 32,000 per card.
- Nine gallery images/card, at most 300 gallery images/document; safe HTTPS image URL length at most 2048.
- Eight actions/group and 128 actions/document including Next; backend state at most 8192.
- Navigation registry: 512 references, 30-minute expiry; continuation chain at most 128 reads.
- Community card/discussion/reply references share the bounded 8192-entry, two-hour registry and revision checks.
- Web feed: at most 1000 distinct cards per open page, with an explicit display-limit notice (not “all loaded”).
- Three empty incremental pages stop automatic advance and leave a manual continuation.
- Back history: at most 64 navigation entries; back re-reads the previous route, not an unbounded snapshot cache.
  It does not restore all previously appended batches after navigating into an internal subpage.
- These are cooperative resource limits, not protection against malicious in-process plugins.

## Verification

```powershell
dotnet build Auralis.sln -c Release --no-restore
dotnet run --project Auralis.Tests -c Release
dotnet run --project Auralis.Platform.Host.Tests -c Release
dotnet run --project plugin-sdk/ContractCheck.Tests -c Release
pwsh -NoProfile -File tools/Test-PluginPageUpgrade.ps1
npm run test:logic
npm run test:ui -- globalpages pages pagefeeds community media
```

The plugin-only upgrade proof freezes the host test runtime and loads two independently built original
plugin DLLs through the production Host and Native coordinator. V2 adds a new page, incremental cards,
gallery data and a comment/reply chain. The two DLL hashes differ; all 15 frozen host runtime files remain
unchanged. Evidence for this run: artifacts/page-upgrade-87cb438f831141a7bb64f0a82449a4f4/result.json.

Host tests cover duplicate IDs, version gates, gallery/count limits, opaque projection, creator context,
append ownership, cyclic cursors, cross-provider discussion rejection, cancellation and revision revocation.
UI tests use real shipped application resources and synthetic data, light/dark at 1/1.5/2 device scale,
including narrow windows, keyboard, reduced motion, append retry/deduplication, galleries, comments/replies,
return focus and missing plugins. These are not real-account, acoustic, installed-MSIX or multi-monitor tests.
No signing, installation, push or GitHub release is implied.

### Completed development checks — 2026-09-13

- Release solution: zero warnings/errors; core services and Host tests passed.
- Provider regression: 50/50; actual split-DLL regression: 54/54 (including migrated page behavior).
- Contract checker: 89 assertions; JavaScript logic: 23/23.
- Six private packages / 11 providers passed static inspection; no installed user state was changed.
- Final core-only publish passed the plugin boundary guard. All seven changed/new Web entry assets
  match source hashes in artifacts/plugin-pages-core-stage2-20260913-final.
- Final published Pages v1 + v2 feed suites: 12 theme/scale cases passed. Source community/media
  compatibility suites: 12 further theme/scale cases passed. Light wide and dark narrow screenshots
  were inspected; the final light capture confirms the header alignment correction.
- Fixed regressions found during testing: focused-navigation disabling leaked Escape to search;
  an empty status paragraph hid the continuation control; the global media-dialog body selector
  targeted a main page; the inherited page header pushed the title to the opposite edge.
- Generated private package output: artifacts/plugin-pages-stage2-20260913. The new page plugin
  needs SDK 2.5; do not import it into the existing SDK 2.2 signed test application.

## Current verification and next steps

### Completed stage 3 development checks — 2026-09-13

- Release solution: zero warnings/errors; core service tests and Host tests passed.
- Provider regressions: 50/50; actual split-DLL regressions: 54/54, including personal-space signed-out,
  synthetic signed-in identity, activity/about navigation and foreign-provider rejection.
- SDK contract checker: 89 assertions; JavaScript logic: 23/23.
- Six private packages / 11 providers passed static inspection. New personal-space plugin: 1.15.0,
  requiring Host SDK 2.6; output artifacts/plugin-pages-stage3-20260913. No user-profile installation.
- Core-only publish artifacts/plugin-pages-core-stage3-20260913 passed the plugin boundary guard;
  changed app.js, plugin-pages.js/css, i18n.js and index.html match source hashes.
- Published-resource UI suites globalpages/pages/pagefeeds/community/media: 30 light/dark × scale cases
  passed. Covers reduced motion, narrow/short windows, empty library, keyboard tabs, cancel/late replies,
  failed-tab retry, sidebar highlighting, multiple global entries, revision and missing configuration/plugins.
- Light-wide and dark-narrow screenshots inspected under artifacts/plugin-global-pages-ui.
- Frozen-host upgrade evidence: artifacts/page-upgrade-eeb8d8cc1efd48a7ad02194c422a09fa/result.json.
  Distinct original plugin DLL revisions; all 15 host runtime files unchanged; v2 adds a global entry and
  three tabs in addition to the earlier discography/discussion proof.
- Not tested: real accounts, audible playback, installed MSIX upgrade, native cross-monitor DPI.
  No signing, push, GitHub release or changes to the user's installed app/profile were performed.

### Previous stage summary

第二阶段已打通：插件声明主页面与作者上下文，返回头像、日期、多图动态、增量列表和评论入口；
通用 UI 接入图库与既有右侧评论/楼中楼。页面与平台请求属于插件，Fluent 与权限边界属于宿主。

第三阶段已补上全局导航和标签分区，第四阶段补上只读搜索/筛选输入；平台专属数据与账号身份仍属于插件。
下一步优先扩展设置分组与安全的跨页上下文。可写表单、关注/点赞/回复/转发仍需单独
权限、用户确认、错误与撤销契约，不能复用只读 action。新控件类型仍可能需要宿主扩展，
不能把“契约内只更新插件”宣传成“任何新功能永远不需要更新播放器”。
