# Creator activity and comment threads

Stage 2 (SDK 2.5 / Abstractions 1.7): creator entries can opt into Pages document v2 and a main-content
presentation, including exact CreatorSearch context. Rich feeds then arrive as plugin-owned documents,
with generic gallery/append components and an opaque Discussion bridge to the existing comment/reply drawer.
The legacy CreatorFeed route below remains the compatibility fallback, not the new plugin's page owner.
See [declarative pages](PLUGIN_PAGES.md). This is read-only browsing, not follow/like/repost/comment submission.

Development revision: 2026-09-13. Not installed or released.

## Main-page browsing and creator search

Host SDK **2.4.0**, Abstractions **1.6.0** add the optional `ICreatorSearchCapability.SearchCreatorsAsync`
and `CreatorSearch` / `creator-search.v1` declaration. No existing interface member or enum ordinal changes.
Search metadata is not a track. Its random creator handle opens the existing creator/feed contract directly;
it cannot resolve a playback lease, enter the queue, or be saved as a song.

The creator/activity view is now a normal main-content route, not a modal dialog. Navigation and bottom
transport remain available; comments retain their right-hand drawer. Cards show author/avatar, full date,
paragraphs and up to nine images. A separate image viewer supports full images and previous/next buttons.
Returning restores the query, loaded results, search-box visibility, scroll and visible originating control.
From the immersive player, opening a creator uses the standard exit-to-audio path and then the main page.

Search has its own 300 ms debounce, cancellation, request identity and serial pagination. Only a selected,
configured provider with CreatorSearch exposes this section. Errors preserve prior results; missing plugins
remove it. Continuations are backend-only, bound to provider/query/revision and limited to 30 minutes with
512 search references. Creator/activity/discussion references inherit authorization revision invalidation.
No raw creator ID, upstream cursor, account material or original artwork URL enters the main WebView.

This is a **read-only browsing phase**, not a full social client. Follow, like, repost and comment submission
are not implemented. Complex activity UI still uses the generic CreatorFeed component rather than the
first declarative Pages schema, which cannot yet express incremental threads or galleries. Provider
business rules remain in the plugin; do not advertise every UI component as independently plugin-owned.

## User experience

- A provider declaring `CreatorFeed` or `CreatorProfile` enables creator-name navigation on search/collection rows and
  the full-screen player's creator name and toolbar. Compact rows keep an accessible creator icon.
- The in-app creator profile is read-only. Activity cards use a two-column grid with independent card
  heights, a one-column narrow layout, shared Fluent surfaces and the main content scroll container.
  Reading a profile does not play a track or change the queue. The profile stays pinned to the selected creator.
- Opening a post's comments places the existing right-hand comment drawer above the profile. Escape
  closes the drawer first, then the profile; focus returns to the respective originating control.
- Comments expose Recommended/Newest ordering and full date/time. `CommentReplies` enables a root's
  paginated thread (including replies to replies) with avatars, recipient attribution when supplied,
  dates and emoji. One thread is expanded at a time; “More replies” reads its next page.
- Root comments and activity append near the scroll bottom; failed pages keep existing nodes and offer
  retry. The former 500-comment display cap is removed. No claim is made that unavailable, deleted,
  moderated or private comments can be recovered, or that changing provider data is a consistent snapshot.
- Current-track comments follow playback as before. Comments opened from a list/post stay pinned.
  Disabling a capability retires its pending requests and corresponding UI without stopping audio.

## Optional plugin contracts

The profile/feed/thread interfaces were introduced by SDK 2.1/2.2; current development is SDK **2.4.0**,
Abstractions **1.6.0**, platform API **1**, manifest schema **5**.
The existing assembly identities are preserved for additive ABI compatibility.

| Capability | Contract | Required host feature |
| --- | --- | --- |
| CreatorProfile | ICreatorProfileCapability: GetCreatorAsync(mediaId) | creator-profile.v1 |
| CreatorFeed | ICreatorFeedCapability: GetCreatorAsync(mediaId), GetCreatorPostsAsync(creatorId,page) | creator-feed.v1 |
| CommentReplies | ICommentRepliesCapability: GetCommentRepliesAsync(entityId,rootCommentId,page) | comment-replies.v1 |
| CreatorSearch | ICreatorSearchCapability: SearchCreatorsAsync(request) | creator-search.v1 |

`PlatformCreatorPost.DiscussionId` is independent of its post ID. Null explicitly means no discussion
entry. `PlatformComment.ReplyToAuthor` is optional plain text. Existing providers need not implement either
new interface. Manifest discovery and import remain inert. Older hosts reject the new required features.

Profile-only providers show a name, biography and avatar, without an activity heading or feed request.
When both capabilities are declared, profile resolution uses CreatorProfile and activities use CreatorFeed.
Existing CreatorFeed implementations remain compatible; no mandatory interface member changed.

Public image URLs remain backend-only and are subject to the provider's declared artwork policy and
active-route predicate. Only controlled `platform-art.auralis.local` URLs reach Web content. Normalized
display text is escaped, never interpreted as HTML. No remote page is embedded into the creator panel.

## Native/Web additions

`requestPlatformExtras` / `setPlatformExtras` keep the existing envelope and add kinds:

- `creator`: request handle is a track or a current creator search result; items contains one `{handle,displayName,description,avatarUrl}`.
- `feed`: handle is a Native creator reference; items contain `{handle,discussionHandle?,title,text,
  publishedAt?,commentCount?,images}` and an optional opaque `nextPageHandle`.
- `comments`: handle is a track or a Native discussion reference; optional `sort: recommended|newest`.
- `replies`: same subject handle plus a Native `rootHandle` and optional `pageHandle`.

Comment items add `handle`, `replyCount` and `replyToAuthor`, retaining `publishedAt`, avatar and emotes.
Request IDs are positive JavaScript-safe integers. Native binds continuations to kind, subject, root and
sort; mismatches/expired entries fail closed. Community references are memory-only, bounded at 8,192 with
a two-hour TTL. Artwork has its own existing 2,000-entry bounded registry. Expired/evicted references
require reopening; they are never persisted in the library, saved playlists or browser storage.

All operations pass through the current Host route. The main app contains no provider endpoints,
site identifiers, signing, login, or response parsing. Reading activity/comments never mutates an account.

## Verification

- `dotnet run --project Auralis.Platform.Host.Tests -c Release`: synthetic real-DLL routing, reference
  kind/context/sort binding, expiry, deduplication and unapproved artwork rejection.
- `npm run test:ui -- community media`: isolated Edge bridge tests; light/dark, 1/1.5/2 device scale,
  narrow/short windows, reduced motion, keyboard, error/retry, late responses, revocation, dates, threads,
  more than 500 root comments and no playback side effects. Screenshots are in `artifacts/community-ui`.
- The private provider suite tests provider-specific parsing and paging separately; it is not required
  to build or test this public core.

These browser cases are **not** native multi-monitor PerMonitorV2 DPI acceptance. Authenticated
server behavior still requires a live integration pass. Initial anonymous TLS checks failed in the
restricted Windows process; approved execution outside it later succeeded without disabling certificate
validation. Private integration evidence and platform access limits are recorded in the private repository.

### Development verification record (2026-09-13)

- Release solution build and core-only framework-dependent publish passed. Final assets are in the
  generated `artifacts/creator-social-core-20260913-final`; all seven changed Web resources match source hashes.
- Core service and Host tests passed; community Host checks now include direct creator search, exact
  creator navigation without a media record, query-bound pagination and rejection of cross-kind handles.
- Original private provider regression: 50/50. Fresh real split-DLL synthetic regression: 54/54.
  Creator search fixtures cover string UIDs above 2^53, public metadata normalization, paging, invalid
  cursors and denied upstream responses. Six package static inspections and 89 SDK checker assertions passed.
- Final published community UI passed light/dark × 100%/150%/200% device scale, including main-page
  presentation, gallery, search pagination, return-query/results/focus, stale query rejection, missing
  plugin UI, dates, replies and existing >500-comment pagination. Wide light and narrow dark captures
  were inspected. Existing media and declarative-page suites passed on the published candidate before
  the final community-only single-entry-animation adjustment; community was then rerun on final assets.
- JavaScript logic: 23/23. Final core publish passed provider/build-input boundary checks. `git diff --check` passed.
- These are isolated synthetic browser/API checks, **not live Bilibili account, acoustic playback,
  native multi-monitor DPI or MSIX upgrade acceptance**. No installed-app replacement, GitHub push,
  signing or release was performed. Follow/like/repost/comment-writing remain future work.

### Development verification record (2026-09-12)

- Pure-core solution Release build: zero errors/warnings; plugin boundary checks passed, including the
  core-only self-contained published output. Public-core Host tests passed, including 16 community checks.
- Core service tests passed on rerun. The first public-checkout run failed during temporary media fixture
  cleanup because a file was still held open; no assertion was removed and no test file was force-deleted.
- Private provider regression: 49/49; freshly built split-plugin regression: 53/53.
- Published Web assets: community and existing media suites passed in six light/dark device-scale cases
  each. Community was rerun with simultaneous creator/comment/video row actions and action-column bounds
  assertions after adding wrapping. These use synthetic data, not live creator content.
- No application installation, repository push or release was performed for this development revision.

Follow-up: invalid provider pagination must remain an error, not become an empty/complete discussion.
The community UI regression now verifies that an invalid next page preserves 20 existing comments,
shows the localized error, and resumes pagination on an explicit successful retry.

## 中文说明

作者主页和动态为通用可选能力，评论沿用右侧抽屉。动态是响应式卡片网格，窄窗口变为单列；
评论显示日期，可展开楼中楼并继续读取下一页，不再限制只显示 500 条。只呈现平台当前允许访问的内容，
不声称能读取已删除或受限评论。新增插件要求 Host SDK 2.1.0；旧播放器需配套更新后才能使用新能力。
