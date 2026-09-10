(() => {
  'use strict';

  const STORAGE_KEY = 'auralis:language';
  const preferences = new Set(['system', 'zh-CN', 'en-US']);
  const supportedLanguages = new Set(['zh-CN', 'en-US']);

  const namedCatalog = {
    'zh-CN': {
      'language.label': '语言',
      'language.description': '更改 Auralis 的界面语言',
      'language.system': '跟随 Windows',
      'language.zh-CN': '简体中文',
      'language.en-US': 'English (United States)',
      'search.pagination': '在线搜索分页',
      'search.page': '第 {page} 页',
      'search.pageWithTotal': '第 {page} 页 · 共 {total} 条',
      'search.previousPage': '上一页',
      'search.nextPage': '下一页',
      'search.nextPageFailed': '无法读取下一页'
    },
    'en-US': {
      'language.label': 'Language',
      'language.description': 'Change the Auralis display language',
      'language.system': 'Use Windows language',
      'language.zh-CN': 'Simplified Chinese',
      'language.en-US': 'English (United States)',
      'search.pagination': 'Online search pages',
      'search.page': 'Page {page}',
      'search.pageWithTotal': 'Page {page} · {total} results',
      'search.previousPage': 'Previous page',
      'search.nextPage': 'Next page',
      'search.nextPageFailed': 'Could not load the next page'
    }
  };

  // Existing Chinese source strings intentionally remain valid lookup keys. This lets the UI be
  // migrated incrementally without translating user metadata, provider payloads, or lyrics.
  const englishSourceCatalog = Object.freeze({
    '传输组件请使用设置中的“导入传输组件”按钮；不要与平台插件混合导入。': 'Use Import transport component in settings. Do not mix transport packages with platform plugins.',
    '传输组件': 'Media transport components',
    '选择媒体下载与缓存组件；更改在重启后生效。': 'Choose media download and cache transport. Changes apply after restart.',
    '导入传输组件': 'Import transport component',
    '正在处理传输组件…': 'Working on transport components…',
    '支持 .auralis-transport.zip；导入后默认关闭，不影响当前播放。': 'Supports .auralis-transport.zip. Imports start disabled and do not interrupt playback.',
    '本次启动传输组件': 'Transport selected for this run',
    '保留随包媒体传输，不需要导入组件。': 'Bundled media transport remains available without importing a component.',
    '启用传输组件': 'Enable transport component',
    '尚未导入传输组件': 'No transport components imported',
    '更改已保存，重启后使用新的传输组件。当前播放会停止。': 'Changes saved. Restart to use the new transport component. Playback will stop.',
    '确认导入传输组件': 'Confirm transport component import',
    '我信任此传输组件的来源，了解它可处理媒体地址与请求头，并在播放器进程内运行，不是安全沙箱。': 'I trust this transport component. It can process media URLs and request headers and runs in the player process, not a security sandbox.',
    '播放组件请使用设置中的“导入播放组件”按钮；不要与平台插件混合导入。': 'Use Import playback component in settings for playback packages. Do not mix them with platform plugins.',
    '播放组件': 'Playback components',
    '选择音视频解码组件；更改在重启后生效。': 'Choose an audio/video decoder. Changes apply after restart.',
    '导入播放组件': 'Import playback component',
    '正在处理播放组件…': 'Working on playback components…',
    '支持 .auralis-playback.zip；导入后默认关闭，不影响当前播放。': 'Supports .auralis-playback.zip. Imports start disabled and do not interrupt playback.',
    '安装记录不可用，请检查文件权限并刷新。': 'Installation records are unavailable. Check file permissions and refresh.',
    '当前播放组件': 'Current playback component',
    '随包默认': 'Bundled default',
    '外置组件': 'External component',
    '外置组件不可用，已回退随包默认。': 'The external component is unavailable. Using the bundled default.',
    '使用随包默认': 'Use bundled default',
    '保留离线基础播放，不需要导入组件。': 'Basic offline playback remains available without importing a component.',
    '已选择': 'Selected',
    '设为下次启动': 'Use after restart',
    '文件不可用或组件不兼容': 'Files unavailable or component incompatible',
    '启用播放组件': 'Enable playback component',
    '尚未导入播放组件': 'No playback components imported',
    '更改已保存，重启后使用新的播放组件。当前播放会停止。': 'Changes saved. Restart to use the new playback component. Playback will stop.',
    '确认导入播放组件': 'Confirm playback component import',
    '我信任此播放组件的来源，了解它将在播放器进程内运行，不是安全沙箱。': 'I trust this component and understand that it runs in the player process, not a security sandbox.',
    '公开歌单 · 无需登录': 'Public collections · No sign-in required',
    '请先完成插件设置': 'Complete the plugin settings first',
    '公开歌单来源': 'Public collection sources',
    '部分来源需要配置': 'Some sources need configuration',
    '部分来源需要处理': 'Some sources need attention',
    '前往设置': 'Open settings',
    '刷新来源，或在插件设置中完成所需配置。': 'Refresh the source, or complete its required plugin settings.',
    '尚未启用歌单插件': 'No playlist plugin enabled',
    '导入并启用支持歌单的插件，重启后将在这里显示对应平台。': 'Import and enable a playlist plugin, then restart to see its platform here.',
    '管理插件': 'Manage plugins',
    '立即重启': 'Restart now',
    '高级组件': 'Advanced components',
    '播放与媒体传输已内置，通常无需调整。仅在使用自定义组件时展开。': 'Playback and media transport are built in. Usually no changes are needed; expand only for custom components.',
    '导入后默认关闭；启用需重启，停用会立即停止新请求并清理登录数据。': 'Imports stay disabled. Enabling requires a restart; disabling immediately blocks new requests and clears sign-in data.',
    '已停用 · 重启后释放': 'Disabled · Restart to unload',
    '已停用并清除该插件的登录数据，重启后释放已加载的组件。': 'Disabled and sign-in data cleared. Restart to unload the component.',
    '登录数据尚未完全清除，请重试。': 'Sign-in data has not been fully cleared. Please retry.',
    '启用所需插件后，重启以应用更改。当前播放会停止。': 'Enable the plugins you need, then restart to apply changes. Playback will stop.',
    '重试清理登录数据': 'Retry sign-in data cleanup',
    '插件已停用，但登录数据未完全清除。请再次执行停用以重试。': 'Plugin disabled, but sign-in cleanup is incomplete. Retry cleanup.',
    '设置已保存，重启后生效。': 'Settings saved. Restart to apply.',
    '停用会清除该插件的登录数据，不删除音乐或收藏；更新保留旧包。': 'Disabling clears this plugin’s sign-in data, not music or favorites. Updates retain the old package.',
    '批量导入插件': 'Import plugin packages',
    '同批插件 ID 重复，请只保留一个版本。': 'Duplicate plugin ID. Keep only one version.',
    '超过批次大小限制。': 'Batch size limit exceeded.',
    '插件需要更新的播放器 SDK，请更新播放器后重试。': 'This plugin requires a newer player SDK. Update the player and try again.',
    '播放器缺少插件必需的功能，请更新播放器或选择兼容的插件版本。': 'The player lacks a required plugin feature. Update the player or choose a compatible plugin version.',
    '插件接口版本与播放器不兼容，请选择匹配的版本。': 'The plugin API is incompatible with this player. Choose a matching version.',
    '播放器不支持此插件清单版本，请更新播放器或选择兼容包。': 'This plugin manifest version is unsupported. Update the player or choose a compatible package.',
    '与播放器不兼容': 'Incompatible with player',
    '最低播放器 SDK': 'Minimum player SDK',
    '必需宿主功能': 'Required host features',
    '文件无效、已变化或不兼容，未导入。': 'Invalid, changed or incompatible file. Not imported.',
    '已导入 · 未启用': 'Imported · Disabled',
    '待确认': 'Awaiting approval',
    '仅导入下方校验通过的插件；错误项不会安装。': 'Only valid plugins below will be imported. Failed items will not be installed.',
    '查看声明能力和 SHA-256': 'View declared capabilities and SHA-256',
    '旧账号兼容访问': 'Legacy account access',
    '以下插件请求访问列出的旧凭据地址，以保留登录并支持刷新和退出。这里只显示地址，不显示 Cookie 或令牌；不信任时请取消导入。': 'These plugins request the listed legacy credential addresses to preserve sign-in, refresh sessions and sign out. Only addresses are shown, never cookies or tokens. Cancel if you do not trust the plugins.',
    '同时批准上面列出的旧账号兼容访问。': 'I also approve the legacy account access listed above.',
    '我信任本批次所有有效插件的来源，了解它们将在播放器进程内运行。': 'I trust the sources of all valid plugins in this batch and understand they will run inside the player process.',
    '拖入不可用，请使用批量导入按钮选择文件。': 'Drag and drop is unavailable. Use the import button to select files.',
    '请先完成或取消当前插件导入。': 'Complete or cancel the current plugin import first.',
    '松开以预览插件包，不会自动安装或启用': 'Drop to review plugin packages. Nothing will be installed or enabled automatically.',
    '请拖入 1–16 个 .auralis-plugin 或 ZIP 插件包。': 'Drop 1–16 .auralis-plugin or ZIP plugin packages.',
    '可多选文件，或拖入应用窗口。每批最多 16 个包、256 MiB；导入后默认关闭。': 'Select multiple files or drop them into the app window. Up to 16 packages and 256 MiB per batch; imports stay disabled.',
    '批次处理完成，请查看各文件结果。成功导入的插件默认关闭。': 'Batch processed. Check each file result. Imported plugins remain disabled.',
    '已启用': 'Enabled',
    '未启用': 'Disabled',
    '待启用 · 重启生效': 'Enable after restart',
    '启用插件': 'Enable plugin',
    '正在处理插件…': 'Processing plugin…',
    '确认导入插件': 'Review plugin import',
    '包文件 SHA-256（请与可信来源核对）': 'Package SHA-256 (check against your trusted source)',
    '我信任此插件来源，了解它将在播放器进程内运行。': 'I trust this source and understand the plugin will run inside the player process.',
    '确认导入': 'Confirm import',
    '取消': 'Cancel',
    '导入插件包': 'Import plugin package',
    '选择 .auralis-plugin 或兼容 ZIP，核对信息并确认信任。': 'Select an .auralis-plugin or compatible ZIP, review the details and confirm trust.',
    '打开需要的插件开关，再从托盘退出并重启应用。': 'Enable the plugins you need, then quit from the tray and restart the app.',
    '操作未确认，请刷新状态后重试。': 'The result is unconfirmed. Refresh the status before retrying.',
    '操作未完成或结果未确认。请检查包格式、完整性、兼容性和文件权限，并刷新状态。': 'The operation is incomplete or unconfirmed. Check package format, integrity, compatibility and file permissions, then refresh the status.',
    '导入完成，默认未启用。请打开所需插件开关并重启应用。': 'Imported and disabled by default. Enable the plugin and restart the app.',
    '平台插件': 'Platform plugins',
    '按已启用插件提供账号、搜索与配置选项': 'Accounts, search and settings provided by enabled plugins',
    '按已启用插件显示功能': 'Features provided by enabled plugins',
    '尚未启用在线插件': 'No online plugins enabled',
    '暂无可用的在线设置。导入并启用插件后，按其声明的功能显示。': 'No online settings are available. Import and enable a plugin to see the features it declares.',
    '仅列出已启用且支持搜索的插件。': 'Only enabled plugins with search support are listed.',
    '此插件未提供交互式登录。': 'This plugin does not provide interactive sign-in.',
    '插件设置已保存': 'Plugin settings saved',
    '保存尚未确认，请刷新设置后重试。': 'Save has not been confirmed. Refresh settings before retrying.',
    '账号功能由对应插件提供；停用会清除该插件的登录资料，不删除本地音乐与收藏。': 'Account features are provided by the plugin. Disabling it clears its sign-in data without deleting local music or saved tracks.',
    '弹幕（音频与视频）': 'Timed comments (audio and video)',
    '内嵌在全屏播放器中': 'Embedded in the fullscreen player',
    '查看安装状态、完整性与更新方式': 'Installation status, integrity and updates',
    '已安装的插件': 'Installed plugins',
    '校验通过不代表已登录或平台服务可用': 'Integrity checks do not verify account sign-in or service availability',
    '需要升级插件': 'Plugin update required',
    '此插件使用旧清单，不能在当前播放器中启用。请导入同一插件 ID 的新版包，确认后启用并重启；已有收藏、设置和账号数据会保留。': 'This plugin uses an older manifest and cannot be enabled in this player. Import an updated package with the same plugin ID, review it, then enable and restart. Existing favorites, settings and account data are preserved.',
    '正在检查插件…': 'Checking plugins…',
    '无法读取插件状态，请重试': 'Could not read plugin status. Please retry.',
    '检查仅读取清单与文件，不登录账号或访问平台网络': 'Only manifests and files are checked. No sign-in or platform requests.',
    '校验通过': 'Integrity verified',
    '未批准或文件已变化': 'Unapproved or changed',
    '重启后识别': 'Restart required',
    '文件缺失或冲突': 'Missing files or conflict',
    '尚未发现平台插件': 'No platform plugins found',
    '本地音乐、歌单和播放不受影响；在线收藏条目会保留。': 'Local music and playback are unaffected. Saved online tracks are retained.',
    '部分插件无法识别，请检查版本、重复安装或损坏文件。': 'Some plugins could not be recognized. Check compatibility, duplicate installs or damaged files.',
    '安装与更新': 'Install and update',
    '只安装你信任的插件': 'Only install plugins you trust',
    '插件在播放器进程内运行，不是安全沙箱。SHA-256 仅用于核对文件完整性。': 'Plugins run inside the player process, not a sandbox. SHA-256 only checks file integrity.',
    '从可信来源获取插件包与 SHA-256。': 'Obtain the package and SHA-256 from a trusted source.',
    '在托盘中退出播放器，使用安装工具核对摘要并明确批准。': 'Quit the player from the tray. Verify the hash and explicitly approve with the installer.',
    '重新打开 Auralis；原有账号、收藏与本地音乐保留。': 'Reopen Auralis. Existing accounts, saved tracks and local music are retained.',
    '用户插件文件夹': 'User plugin folder',
    '仅复制 DLL 不会获得批准；当前版本不提供热更新或自动下载。': 'Copying a DLL does not approve it. Live updates and automatic downloads are not supported.',
    '无法打开插件文件夹': 'Could not open the plugin folder',
    '云母': 'Mica',
    '云母使用 Windows 系统材质；不支持时显示纯色背景': 'Mica uses Windows system material; unsupported systems use a solid background',
    '支持视频的平台 · 内嵌在全屏播放器中': 'Supported video providers · inside the fullscreen player',
    '我的歌单': 'My playlists',
    '加入歌单': 'Add to playlist',
    '加入我的歌单': 'Add to my playlists',
    '新建歌单': 'New playlist',
    '歌单名称': 'Playlist name',
    '创建': 'Create',
    '分 P': 'Parts',
    '评论': 'Comments',
    '查看评论': 'View comments',
    '播放扩展': 'Playback extras',
    '封面点击切换视频': 'Open video when clicking cover',
    '最近搜索': 'Recent searches',
    '清空历史': 'Clear history',
    '重试视频': 'Retry video',
    '视频画面准备中；暂停时可点击播放继续': 'Preparing the picture. Press Play to continue when paused.',
    '随播放进度显示；减少动态效果时使用静态文字': 'Follow playback; use static text with reduced motion',
    '← 返回音频': '← Back to audio',
    '正在加载视频，音频继续播放…': 'Loading video; audio continues…',
    '正在读取…': 'Loading…',
    '暂无评论。': 'No comments yet.',
    '加载更多': 'Load more',
    '已加载全部评论': 'All comments loaded',
    '正在准备音频…': 'Preparing audio…',
    '已显示 500 条评论': 'Showing 500 comments',
    '已加入歌单': 'Added to playlist',
    '从歌单移除': 'Remove from playlist',
    '在全屏播放器内播放视频': 'Play video inside the fullscreen player',
    '全屏播放器内播放视频': 'Play video inside the fullscreen player',
    '本地文件与多平台曲目 · 在线曲目按平台权限播放': 'Local files and online tracks · provider access rules apply',
    '从歌曲列表的“加入歌单”按钮添加音乐。': 'Use Add to playlist on a track to add music.',
    '创建你的第一个歌单，把喜欢的音乐放在一起。': 'Create a playlist to keep your favorite music together.',
    'Auralis 正在启动': 'Auralis is starting',
    '本地音乐播放器': 'Local music player',
    '你的本地音乐': 'Your local music',
    '最小化': 'Minimize',
    '最大化': 'Maximize',
    '还原': 'Restore',
    '关闭': 'Close',
    '本地音乐': 'Local music',
    '主导航': 'Main navigation',
    '搜索': 'Search',
    '歌曲': 'Songs',
    '专辑': 'Albums',
    '艺术家': 'Artists',
    '歌单': 'Playlists',
    '我喜欢的': 'Favorites',
    '最近播放': 'Recently played',
    '在线歌单': 'Online playlists',
    '你的音乐收藏，汇聚在这里': 'Your music collections, together',
    '与本地音乐库分离': 'Separate from your local library',
    '筛选在线平台': 'Filter online platforms',
    '管理平台账号': 'Manage platform accounts',
    '部分账号需要处理': 'Some accounts need attention',
    '歌单与收藏夹': 'Playlists and favorites',
    '还没有可显示的歌单': 'No playlists to show yet',
    '连接平台账号，或刷新已连接账号的收藏。': 'Connect an account or refresh its collections.',
    '在线平台歌单': 'Online platform playlists',
    '在线 · 与本地分离': 'Online · kept separate from local music',
    '个人歌单 · 与本地分离': 'Personal playlists · kept separate from local music',
    '设置': 'Settings',
    '搜索歌曲、艺术家或专辑': 'Search songs, artists, or albums',
    '清空搜索': 'Clear search',
    '切换主题': 'Switch theme',
    '添加音乐': 'Add music',
    '展开正在播放': 'Open Now Playing',
    '还没有播放音乐': 'Nothing is playing',
    '从音乐库中选择一首歌曲': 'Choose a song from your library',
    '随机播放': 'Shuffle',
    '上一首': 'Previous',
    '播放': 'Play',
    '暂停': 'Pause',
    '下一首': 'Next',
    '播放列表': 'Play queue',
    '播放进度': 'Playback position',
    '收藏当前歌曲': 'Favorite current song',
    '桌面歌词': 'Desktop lyrics',
    '音量': 'Volume',
    '倍速播放': 'Playback speed',
    '播放队列': 'Play queue',
    '关闭播放队列': 'Close play queue',
    '收起详情页': 'Collapse Now Playing',
    '置顶': 'Always on top',
    '全屏播放器主题': 'Full-screen player theme',
    '跟随系统主题': 'Use system theme',
    '切换到深色主题': 'Switch to dark theme',
    '切换到浅色主题': 'Switch to light theme',
    '全屏 F11': 'Full screen F11',
    '歌曲与歌词': 'Song and lyrics',
    '未知艺术家': 'Unknown artist',
    '本地歌词': 'Local lyrics',
    '歌词': 'Lyrics',
    '等待播放': 'Waiting to play',
    '本地歌词优先，在线歌词默认关闭': 'Local lyrics take priority; online matching is off by default',
    '唱片封面': 'Record cover',
    '暂无歌词': 'No lyrics',
    '播放模式': 'Playback mode',
    '歌词来源': 'Lyrics source',
    '使用本地歌词': 'Use local lyrics',
    '为当前歌曲匹配在线歌词': 'Match online lyrics for this song',
    '本地': 'Local',
    '在线': 'Online',
    '歌词选项': 'Lyrics options',
    '当前歌曲歌词': 'Lyrics for this song',
    '选择本地歌词文件': 'Choose a local lyrics file',
    '支持 .lrc 与 .txt，优先用于这首歌': 'Supports .lrc and .txt and takes priority for this song',
    '恢复自动匹配': 'Restore automatic matching',
    '移除这首歌指定的本地歌词': 'Remove the local lyrics override for this song',
    '清除歌词缓存': 'Clear lyrics cache',
    '只清除当前歌曲，不影响其他歌曲': 'Clear only this song; other songs are not affected',
    '逐歌曲歌词缓存': 'Per-song lyrics cache',
    '关闭逐歌曲歌词缓存': 'Close per-song lyrics cache',
    '搜索歌曲、艺术家或歌词来源': 'Search songs, artists, or lyrics sources',
    '搜索逐歌曲歌词缓存': 'Search per-song lyrics cache',
    '刷新': 'Refresh',
    '让本地音乐有个好看的家。': 'A beautiful home for your local music.',
    '本地曲库始终只属于你；需要时也可以在搜索页临时查找在线歌曲，不会把它们写入本地音乐库。': 'Your local library stays yours. Online results are temporary and are never written to it.',
    '添加文件夹': 'Add folder',
    '首本地歌曲': 'local songs',
    '张专辑': 'albums',
    '位艺术家': 'artists',
    '最近聆听': 'Recently played',
    '开始你的音乐库': 'Start your music library',
    '查看全部': 'View all',
    '这里还很安静': 'It is quiet here',
    '添加几个音频文件，或选择一个文件夹。Auralis 只会读取你亲自选择的本地内容。': 'Add some audio files or choose a folder. Auralis reads only the local content you select.',
    '在线来源': 'Online source',
    '在线搜索来源': 'Online search source',
    '标题': 'Title',
    '时长': 'Duration',
    '来源': 'Source',
    '操作': 'Actions',
    '不可播放': 'Unavailable',
    '试听': 'Preview',
    '仅提供试听片段': 'Preview clip only',
    '视频音轨 · 仅播放声音 · 不会加入本地曲库': 'Video audio track · audio only · not added to the local library',
    '在线结果 · 不会加入本地曲库': 'Online result · not added to the local library',
    '新窗口播放 MV': 'Play MV in a new window',
    '新窗口播放视频': 'Play video in a new window',
    '继续输入即可搜索在线歌曲': 'Keep typing to search online songs',
    '至少输入两个字符，减少不必要的网络请求。': 'Enter at least two characters to avoid unnecessary network requests.',
    '正在检查在线搜索配置': 'Checking online search configuration',
    '本机音乐结果不受影响。': 'Local music results are not affected.',
    '在线搜索尚未配置': 'Online search is not configured',
    '本地搜索可以继续使用；在设置中填写你信任的 Auralis gateway 后才会联网。': 'Local search still works. Add a trusted Auralis gateway in Settings before other sources can connect.',
    '前往设置': 'Open Settings',
    '查询只发送给当前选择的在线来源。': 'The query is sent only to the selected online source.',
    '在线结果暂时不可用': 'Online results are temporarily unavailable',
    '重试': 'Retry',
    '本地结果仍会保留，可以换一个关键词或在线来源。': 'Local results remain available. Try another keyword or online source.',
    '视频仅解析音频': 'Video results are played as audio only',
    '仅搜索与取流': 'Search and streaming only',
    '在线歌曲': 'Online songs',
    '正在读取个人歌单': 'Loading personal playlists',
    '歌单只用于当前在线浏览，不会合并到本地歌单。': 'These playlists are for online browsing only and are not merged into local playlists.',
    '刷新个人歌单': 'Refresh personal playlists',
    '刷新并返回歌单': 'Refresh and return to playlists',
    '已请求刷新歌单，请从列表重新选择。': 'Playlist refresh requested. Choose a playlist from the list again.',
    '歌单中没有可显示的歌曲': 'No displayable songs in this playlist',
    '搜索音乐': 'Search music',
    '本机音乐即时过滤；在线来源仅在这里返回搜索数据与临时媒体流': 'Local music is filtered instantly. Online sources return search data and temporary media streams only here.',
    '从本地开始，需要时连接在线来源': 'Start locally and connect online sources when needed',
    '输入歌曲名、艺术家、专辑或文件名。本地结果不会离开设备；已配置的在线来源会收到你的搜索关键词。': 'Search by song, artist, album, or file name. Local results stay on this device; configured online sources receive only the search query.',
    '添加本地音乐文件夹': 'Add local music folder',
    '本机音乐库中没有匹配歌曲': 'No matching songs in the local library',
    '在线结果会单独显示在下方，不会被加入本地曲库。': 'Online results appear separately below and are not added to the local library.',
    '播放本地结果': 'Play local results',
    '本机歌曲': 'Local songs',
    '本机专辑': 'Local albums',
    '本机艺术家': 'Local artists',
    '播放全部': 'Play all',
    '添加音乐文件夹': 'Add music folder',
    '切换排序': 'Change sorting',
    '没有找到匹配的音乐': 'No matching music found',
    '换一个歌名、艺术家或专辑关键词试试。': 'Try another song, artist, or album keyword.',
    '取消收藏': 'Remove from favorites',
    '收藏': 'Favorite',
    '从音乐库移除': 'Remove from library',
    '按所在文件夹整理': 'Grouped by folder',
    '音乐库中的声音': 'Artists in your library',
    '选择本地头像': 'Choose local picture',
    '登录已过期，请重新连接': 'Sign-in expired. Reconnect your account.',
    '未连接；公开搜索仍然可用': 'Not connected; public search is still available',
    '未连接；公开搜索与播放仍然可用': 'Not connected; public search and playback are still available',
    '在线音乐': 'Online music',
    '集中浏览已连接平台的个人歌单与收藏夹；播放不会改变本地音乐库': 'Browse playlists and favorites from connected services in one place; playback never changes your local library',
    '云歌单': 'Cloud playlists',
    '个人歌单': 'Personal playlists',
    '收藏夹': 'Favorites',
    '收藏夹 · 纯音频播放': 'Favorites · audio-only playback',
    '在线来源': 'Online source',
    '未连接': 'Not connected',
    '连接已过期': 'Connection expired',
    '断开': 'Disconnect',
    '登录': 'Sign in',
    '重新登录': 'Sign in again',
    '刷新': 'Refresh',
    '重试': 'Retry',
    '正在读取账号与歌单': 'Loading accounts and playlists',
    '本地音乐不受影响。': 'Local music is not affected.',
    '账号需要重新登录': 'Sign-in required',
    '由你创建': 'Created by you',
    '返回在线歌单': 'Back to online playlists',
    '在线内容只用于当前浏览，不会合并到本地歌单。': 'Online items are only used for this view and are never merged into local playlists.',
    '可以在平台中添加内容后再刷新。': 'Add items on the service, then refresh.',
    '在线来源只提供搜索数据、平台歌词与临时音频/MV 流，不会修改本地曲库、收藏或最近播放': 'Online sources provide search data, platform lyrics, and temporary audio/MV streams. They never modify the local library, favorites, or recent history.',
    '在线来源': 'Online source',
    '断开账号': 'Disconnect account',
    '默认在线来源': 'Default online source',
    '仅影响搜索页；本机搜索始终同时保留': 'Affects only Search; local results are always kept',
    '保存中…': 'Saving…',
    '保存': 'Save',
    '请输入 HTTPS 地址；只有本机 localhost 可以使用 HTTP。': 'Enter an HTTPS address. Only local localhost addresses may use HTTP.',
    '快捷键已恢复默认': 'Keyboard shortcuts restored to defaults',
    '请按键…': 'Press a key…',
    '设置主页': 'Settings home',
    '返回设置主页': 'Back to Settings',
    '按功能分类，进入二级页面后再调整具体选项': 'Choose a category to adjust its options',
    '外观与个性化': 'Appearance & personalization',
    '主题、强调色、窗口材质和列表密度': 'Theme, accent color, window material, and list density',
    '播放与快捷键': 'Playback & shortcuts',
    '启动行为、播放恢复和键盘操作': 'Startup behavior, session restore, and keyboard controls',
    '平台账号、音质、默认来源和 gateway': 'Platform accounts, quality, default source, and gateway',
    '全屏播放器': 'Full-screen player',
    '播放器布局、背景、歌词和过渡动画': 'Player layout, background, lyrics, and transitions',
    '歌词来源、缓存和桌面歌词': 'Lyrics sources, cache, and desktop lyrics',
    '音频设备': 'Audio devices',
    '输出后端、播放设备、声道和缓冲': 'Output backend, playback device, channels, and buffering',
    'Windows 集成': 'Windows integration',
    '任务栏组件、托盘、媒体键和开机启动': 'Taskbar widget, tray, media keys, and startup',
    '音乐文件夹与艺术家头像显示': 'Music folders and artist pictures',
    '个账号已连接': 'accounts connected',
    '公开搜索可直接使用': 'Public search is available',
    '可视化预览': 'Visual preview',
    '设置示例，不播放音频': 'Settings example; no audio is played',
    '沉浸封面': 'Immersive cover',
    '唱片与歌词': 'Record & lyrics',
    '还没有持续监听的文件夹': 'No watched folders yet',
    '添加后，Auralis 会在启动时补扫，并自动发现新增、改名或删除的歌曲。': 'After you add one, Auralis scans it at startup and detects added, renamed, or removed songs.',
    '正在监听的音乐文件夹': 'Watched music folders',
    '正在监听': 'Watching',
    '文件夹当前不可访问': 'Folder is currently unavailable',
    '重新扫描': 'Rescan',
    '停止监听': 'Stop watching',
    '外观': 'Appearance',
    '应用主题': 'App theme',
    '与全屏播放器使用相同的昼夜切换动画': 'Uses the same day/night transition as the full-screen player',
    '强调色': 'Accent color',
    '选择播放控件和选中状态的颜色': 'Choose the color for playback controls and selected states',
    '蓝色': 'Blue',
    '绿色': 'Green',
    '橙色': 'Amber',
    '红色': 'Coral',
    '紫色': 'Violet',
    '青色': 'Teal',
    '窗口特效': 'Window effect',
    '使用纯色、亚克力、自定义图片或当前歌曲封面': 'Use a solid color, acrylic, a custom image, or the current cover',
    '无': 'None',
    '亚克力': 'Acrylic',
    '自定义图片': 'Custom image',
    '歌曲背景': 'Song background',
    '自定义窗口背景': 'Custom window background',
    '已选择本地图片': 'Local image selected',
    '选择一张仅保存在本机的图片': 'Choose an image stored only on this device',
    '选择图片': 'Choose image',
    '紧凑歌曲列表': 'Compact song list',
    '在同一页显示更多本地音乐': 'Show more local music on each page',
    '播放设置': 'Playback settings',
    '打开后自动播放音乐': 'Play automatically at startup',
    '启动应用后自动继续播放': 'Resume playback automatically after launch',
    '重启后保存播放列表和当前音乐': 'Restore queue and current song after restart',
    '退出时记住歌曲、进度、音量和播放顺序': 'Remember the song, position, volume, and play order on exit',
    '效果同时用于沉浸封面与唱片歌词布局': 'Effects apply to both immersive cover and record-and-lyrics layouts',
    '播放器样式': 'Player style',
    '随时切换，两种样式都跟随应用主题': 'Switch anytime; both styles follow the app theme',
    '背景效果': 'Background effect',
    '动态模式让封面色彩缓慢流动': 'Dynamic mode slowly moves the cover colors',
    '静态': 'Static',
    '动态': 'Dynamic',
    '封面切换动画': 'Cover transition',
    '切换歌曲时使用所选过渡': 'Use the selected transition when changing songs',
    '淡入淡出': 'Fade',
    '左边滑入滑出': 'Slide from left',
    '左右滑入滑出': 'Alternate sides',
    '沉浸式播放栏': 'Immersive player controls',
    '鼠标移开时淡化中间与侧边控件': 'Fade the center and side controls when the pointer moves away',
    '歌词文字大小': 'Lyrics text size',
    '全屏播放器歌词字号': 'Full-screen lyrics size',
    '歌词大小自适应': 'Adaptive lyrics size',
    '小窗口下自动缩小，避免歌词拥挤': 'Reduce the size in small windows to avoid crowding',
    '歌词字体': 'Lyrics font',
    '选择适合中文歌词阅读的系统字体': 'Choose a system font that is comfortable for lyrics',
    '歌词对齐位置': 'Lyrics position',
    '调整当前歌词在可视区域中的垂直位置': 'Adjust the vertical position of the current line',
    '歌词行模糊效果': 'Lyrics line blur',
    '弱化已播放和即将播放的歌词': 'Soften past and upcoming lyrics',
    '物理弹簧动画': 'Spring animation',
    '当前歌词切换使用弹簧移动效果': 'Use spring motion when the current line changes',
    '背景流动速度': 'Background motion speed',
    '动态背景的移动速度': 'Motion speed for dynamic backgrounds',
    '背景动画帧率': 'Background frame rate',
    '性能不足时可降低此值': 'Lower this value on slower hardware',
    '快捷键': 'Keyboard shortcuts',
    '点击按键后直接按下想使用的键': 'Select a shortcut, then press the key you want to use',
    '恢复默认': 'Restore defaults',
    '播放 / 暂停': 'Play / pause',
    '增大音量': 'Volume up',
    '减小音量': 'Volume down',
    '静音': 'Mute',
    '点击右侧按键后重新录入': 'Select the key on the right to record a new shortcut',
    '歌词偏好': 'Lyrics preferences',
    '本地歌词始终由你控制；在线匹配默认关闭': 'You stay in control of local lyrics; online matching is off by default',
    '歌词来源顺序': 'Lyrics source order',
    '指定歌词始终拥有最高优先级': 'A manually selected lyrics file always has the highest priority',
    '本地文件优先': 'Local files first',
    '已缓存优先': 'Cached lyrics first',
    '仅本地歌词': 'Local lyrics only',
    '歌词时间偏移': 'Lyrics timing offset',
    '正数让歌词稍后出现，负数让歌词提前': 'Positive values delay lyrics; negative values show them earlier',
    '在线歌词匹配': 'Online lyrics matching',
    '开启后只发送标题、艺术家、专辑和时长；不上传音频、路径或封面': 'Sends only title, artist, album, and duration; never audio, paths, or covers',
    '管理缓存': 'Manage cache',
    '独立置顶悬浮窗，拖动位置会在本机保存': 'A separate always-on-top window whose position is stored on this device',
    '启用桌面歌词': 'Enable desktop lyrics',
    '在其他窗口上方显示当前歌词': 'Show current lyrics above other windows',
    '锁定桌面歌词': 'Lock desktop lyrics',
    '锁定后鼠标可穿透歌词窗口': 'Allow pointer input to pass through the lyrics window when locked',
    '字体大小': 'Font size',
    '字重': 'Font weight',
    '主颜色': 'Active color',
    '当前歌词': 'Current line',
    '未播放颜色': 'Upcoming color',
    '下一行歌词': 'Next line',
    '阴影颜色': 'Shadow color',
    '对齐方式': 'Alignment',
    '居左': 'Left',
    '居中': 'Center',
    '居右': 'Right',
    '背景遮罩': 'Background mask',
    '歌词文字背后显示半透明遮罩': 'Show a translucent mask behind the lyrics',
    '背景遮罩亮度': 'Background mask brightness',
    '调节浅色遮罩的明亮程度，不改变歌词文字': 'Adjust the light mask brightness without changing the lyrics text',
    '桌面歌词背景遮罩亮度': 'Desktop lyrics background mask brightness',
    '动画': 'Animation',
    '歌词切换时使用过渡动画': 'Animate lyrics changes',
    '显示翻译': 'Show translation',
    '本地 LRC 含同时间翻译时一并显示': 'Show same-timestamp translations from local LRC files',
    '双行显示': 'Two-line display',
    '同时显示当前行与下一行': 'Show the current and next line together',
    '总是显示歌曲信息': 'Always show song info',
    '在歌词顶部保留歌曲标题与艺术家': 'Keep the title and artist above the lyrics',
    '这是桌面歌词预览': 'Desktop lyrics preview',
    '样式变化会立即同步到悬浮窗': 'Style changes are applied to the floating window immediately',
    '任务栏音乐体验': 'Taskbar music experience',
    '默认保持关闭；启用后以 Windows 任务栏的材质和交互方式显示': 'Off by default. When enabled, it follows the Windows taskbar material and interaction style.',
    '启用任务栏音乐组件': 'Enable taskbar music widget',
    '允许 Auralis 在 Windows 任务栏中显示当前歌曲与播放状态': 'Allow Auralis to show the current song and playback state on the Windows taskbar',
    '播放时自动显示': 'Show automatically during playback',
    '开始播放歌曲后，自动将音乐组件显示在任务栏中': 'Show the widget on the taskbar when playback begins',
    '显示播放控制': 'Show playback controls',
    '在任务栏组件中提供上一首、播放暂停和下一首': 'Provide previous, play/pause, and next controls in the widget',
    '使用随 Auralis 提供的播放后端，只显示真正可用的能力': 'Uses the playback backend included with Auralis and shows only capabilities that are available',
    '刷新设备': 'Refresh devices',
    '输出后端': 'Output backend',
    '默认交给 Windows；更换后端将从下一首歌起完整生效': 'Uses Windows by default. Backend changes fully apply from the next song.',
    '输出设备': 'Output device',
    '输出声道': 'Output channels',
    '默认保持原始立体声': 'Keep the original stereo channels by default',
    '播放缓冲': 'Playback buffer',
    '缓冲越大越抗抖动，但切歌和拖动反应会稍慢': 'Larger buffers resist dropouts but make track changes and seeking respond more slowly',
    '当前音频输出属性': 'Current audio output properties',
    '输出格式': 'Output format',
    '采样率': 'Sample rate',
    '当前音频终端': 'Current audio endpoint',
    'Windows 共享路径估算': 'Windows shared-path estimate',
    'Windows 报告流延迟': 'Windows reported stream latency',
    '音频引擎周期': 'Audio engine period',
    '使用兼容设置': 'Use compatibility settings',
    '开机自动启动': 'Start automatically at sign-in',
    '登录 Windows 后自动运行 Auralis': 'Run Auralis automatically after signing in to Windows',
    '关闭后驻留系统托盘': 'Keep running in the system tray on close',
    '点击关闭只隐藏主窗口并继续播放；在托盘菜单选择“退出”才会完全关闭': 'Closing hides the main window and keeps playback running; choose Exit from the tray menu to quit completely',
    '定时缩小到托盘': 'Minimize to tray on a timer',
    '到达时间后隐藏窗口，播放不会中断': 'Hide the window when the timer ends without interrupting playback',
    '系统媒体控制': 'System media controls',
    '响应键盘播放、上一首和下一首媒体键': 'Respond to play, previous, and next media keys',
    'Windows 默认音乐播放器': 'Windows default music player',
    'Auralis 只注册为候选应用，最终默认项由你在 Windows 设置中选择': 'Auralis registers only as an available app; you choose the default in Windows Settings',
    '在 Windows 中选择': 'Choose in Windows',
    '应用日志': 'Application logs',
    '日志保存在本机，并自动移除敏感信息': 'Logs are stored on this device and sensitive information is automatically redacted',
    '打开日志文件夹': 'Open logs folder',
    '动画效果': 'Motion effects',
    '默认跟随 Windows 的“显示动画”辅助功能设置': 'Follows the Windows animation accessibility setting by default',
    '本地音乐库': 'Local music library',
    '局域网播放器': 'LAN player',
    '浏览器配对、设备访问和本地音乐流': 'Browser pairing, device access, and local music streaming',
    '让同一可信私有网络中的浏览器独立播放这台电脑上的本地音乐': 'Let browsers on the same trusted private network independently play local music from this PC',
    '正在应用…': 'Applying…',
    '正在运行': 'Running',
    '启动失败': 'Could not start',
    '已关闭': 'Off',
    '启用浏览器播放': 'Enable browser playback',
    '默认关闭；关闭、退出应用或网络变化时会撤销访问会话': 'Off by default; disabling, exiting, or changing networks revokes browser sessions',
    '监听端口': 'Listening port',
    '端口冲突时可改为 1024–65535 之间的其他值': 'Choose another value from 1024–65535 if the port is already in use',
    '局域网播放器端口': 'LAN player port',
    '本地曲库': 'Local library',
    '已连接浏览器': 'Connected browsers',
    '当前访问链接有效至': 'Current pairing link valid until',
    '可访问地址': 'Available addresses',
    '复制配对链接': 'Copy pairing link',
    '在本机浏览器预览': 'Preview in this PC’s browser',
    '生成新链接': 'Generate new link',
    '断开全部浏览器': 'Disconnect all browsers',
    '浏览器使用独立播放队列，不会抢占桌面播放器。音频只在局域网内通过 HTTP 传输，请勿在公共 Wi‑Fi、访客网络或端口映射环境中启用；在线平台歌曲与账号数据不会被共享。': 'Browsers use independent queues and do not take over the desktop player. Audio travels over HTTP within the LAN; do not enable this on public Wi-Fi, guest networks, or through port forwarding. Online tracks and account data are never shared.',
    '局域网设置暂时无法保存；本次会话仍会应用': 'LAN settings could not be saved; they still apply for this session',
    '请先启用局域网播放器': 'Enable the LAN player first',
    '访问链接已复制；请只发送给同一可信网络中的设备': 'Pairing link copied; share it only with devices on the same trusted network',
    '无法写入剪贴板，请稍后重试': 'Could not write to the clipboard. Try again later',
    '无法打开默认浏览器；可复制访问链接后手动打开': 'Could not open the default browser. Copy the pairing link and open it manually',
    '同时监听多个文件夹；在线搜索结果永远不会写入这里': 'Watch multiple folders; online search results are never written here',
    '全部重扫': 'Rescan all',
    '停止监听不会删除已经导入的歌曲，也不会移动或修改磁盘上的任何文件。': 'Stopping a folder watch does not remove imported songs or change any files on disk.',
    '艺术家封面高斯模糊': 'Blur artist covers',
    '柔化从歌曲封面自动生成的艺术家封面；自定义头像保持清晰': 'Soften artist images derived from song covers; custom pictures remain sharp',
    '显示艺术家首字': 'Show artist initial',
    '在艺术家封面或自定义头像上叠加艺术家首字': 'Overlay the artist initial on cover-derived and custom pictures',
    '选择选项': 'Choose an option',
    '选择歌词颜色': 'Choose lyrics color',
    '选择颜色': 'Choose color',
    '预设色或输入十六进制颜色': 'Choose a preset or enter a hexadecimal color',
    '十六进制颜色': 'Hexadecimal color',
    '跟随系统': 'Use system default',
    '微软雅黑 UI': 'Microsoft YaHei UI',
    '等线': 'DengXian',
    '黑体': 'SimHei',
    '楷体': 'KaiTi',
    '自动选择': 'Automatic',
    '标准音质 · 128 kbps': 'Standard · 128 kbps',
    '高品质 · 320 kbps': 'High · 320 kbps',
    '无损 · FLAC': 'Lossless · FLAC',
    '无损': 'Lossless',
    '码率未知': 'Bitrate unknown',
    '编码音频平均码率': 'Average encoded audio bitrate',
    '当前音质': 'Current audio quality',
    '跟随 Windows': 'Use Windows setting',
    '完整动画': 'Full motion',
    '减少动画': 'Reduced motion',
    '不启用': 'Off',
    '15 分钟后': 'After 15 minutes',
    '30 分钟后': 'After 30 minutes',
    '45 分钟后': 'After 45 minutes',
    '1 小时后': 'After 1 hour',
    '1.5 小时后': 'After 1.5 hours',
    '2 小时后': 'After 2 hours',
    '立体声': 'Stereo',
    '左右声道交换': 'Swap left and right channels',
    '仅左声道': 'Left channel only',
    '仅右声道': 'Right channel only',
    '空格': 'Space',
    '在线服务暂时不可用': 'The online service is temporarily unavailable',
    '这个在线结果已经失效，请重新搜索': 'This online result has expired. Search again.',
    '实际音质': 'Actual quality',
    '正在匹配': 'Matching',
    '暂无同步': 'Not synchronized',
    '在线歌词': 'Online lyrics',
    '自动匹配': 'Automatic matching',
    '已找到': 'Found',
    '已有缓存': 'Cached',
    '扫描同目录 LRC 与音频内嵌歌词': 'Scan sidecar LRC and embedded lyrics',
    '正在查找歌词': 'Finding lyrics',
    '正在扫描同目录 LRC 与音频内嵌歌词': 'Scanning sidecar LRC and embedded lyrics',
    '正在通过已启用的歌词插件匹配；不会上传音频文件': 'Matching with enabled lyrics plugins; the audio file is never uploaded',
    '本地歌词 → 已缓存歌词 → 已启用的歌词插件': 'Local lyrics → cached lyrics → enabled lyrics plugins',
    '正在检查同目录 LRC 与音频内嵌歌词': 'Checking sidecar LRC and embedded lyrics',
    '纯音乐': 'Instrumental',
    '静态歌词': 'Unsynchronized lyrics',
    '纯音乐，请欣赏': 'Instrumental track',
    '暂无可用歌词': 'No lyrics available',
    '没有找到匹配歌词': 'No matching lyrics found',
    '可在设置中启用在线歌词匹配': 'Enable online lyrics matching in Settings',
    '正在获取平台歌词': 'Loading platform lyrics',
    '歌词只用于当前播放，不会写入本地歌词缓存。': 'Lyrics are used only for the current playback and are not written to the local lyrics cache.',
    '已添加到“我喜欢的”': 'Added to Favorites',
    '已取消收藏': 'Removed from Favorites',
    '播放队列还是空的': 'The play queue is empty',
    '按添加顺序排列': 'Sorted by date added',
    '按歌曲名称排列': 'Sorted by song title',
    '按艺术家排列': 'Sorted by artist',
    '按专辑排列': 'Sorted by album',
    '正在刷新 Windows 音频设备…': 'Refreshing Windows audio devices…',
    '已应用立体声与 1500 ms 稳定缓冲，下一首歌完整生效': 'Applied stereo with a stable 1500 ms buffer; it fully applies from the next song',
    '已取消定时缩小': 'Tray timer cancelled',
    '输出后端将从下一首歌起完整生效': 'The output backend will fully apply from the next song',
    '已启用在线歌词：仅发送歌曲匹配所需元数据': 'Online lyrics enabled; only metadata needed for matching is sent',
    '已关闭在线歌词：之后只读取本地与已缓存歌词': 'Online lyrics disabled; only local and cached lyrics will be used',
    '关闭主窗口后 Auralis 将驻留系统托盘': 'Auralis will stay in the system tray when the main window closes',
    '关闭主窗口将完全退出 Auralis': 'Closing the main window will quit Auralis',
    '已应用本地窗口背景': 'Local window background applied',
    '桌面歌词已锁定': 'Desktop lyrics locked',
    '桌面歌词已解锁，可以拖动和调整大小': 'Desktop lyrics unlocked; you can move and resize them',
    '这首在线歌曲暂时无法播放': 'This online song is temporarily unavailable',
    '这个 MV 暂时无法播放': 'This MV is temporarily unavailable',
    '无法保存在线搜索配置': 'Could not save online search settings'
  });

  function normalizePreference(value) {
    return preferences.has(value) ? value : 'system';
  }

  function normalizeResolvedLanguage(value) {
    if (supportedLanguages.has(value)) return value;
    const normalized = String(value || '').toLowerCase();
    return normalized.startsWith('zh') ? 'zh-CN' : 'en-US';
  }

  function resolveSystemLanguage() {
    const languages = Array.isArray(navigator.languages) && navigator.languages.length
      ? navigator.languages
      : [navigator.language];
    return normalizeResolvedLanguage(languages.find(Boolean) || 'en-US');
  }

  let preference = normalizePreference(localStorage.getItem(STORAGE_KEY));
  let resolvedLanguage = preference === 'system' ? resolveSystemLanguage() : preference;
  let applying = false;
  let scheduled = false;
  const pendingRoots = new Set();
  const textSources = new WeakMap();
  const attributeSources = new WeakMap();

  function interpolate(template, args = {}) {
    return String(template).replace(/\{([a-zA-Z][\w]*)\}/g, (match, name) =>
      Object.prototype.hasOwnProperty.call(args, name) ? String(args[name]) : match);
  }

  function number(value, options) {
    return new Intl.NumberFormat(resolvedLanguage, options).format(Number(value) || 0);
  }

  function count(noun, value) {
    const numeric = Math.max(0, Number(value) || 0);
    if (resolvedLanguage === 'zh-CN') {
      const classifier = { song: '首歌曲', track: '首', album: '张', artist: '位', account: '个' }[noun] || '个';
      return `${number(numeric)} ${classifier}`;
    }
    const words = {
      song: ['song', 'songs'], track: ['track', 'tracks'], album: ['album', 'albums'],
      artist: ['artist', 'artists'], account: ['account', 'accounts']
    }[noun] || ['item', 'items'];
    return `${number(numeric)} ${numeric === 1 ? words[0] : words[1]}`;
  }

  function dynamicEnglish(source) {
    let match;
    if ((match = source.match(/^(\d+) 首歌曲$/))) return count('song', match[1]);
    if ((match = source.match(/^(\d+) 首$/))) return count('track', match[1]);
    if ((match = source.match(/^(\d+) 张$/))) return count('album', match[1]);
    if ((match = source.match(/^(\d+) 位$/))) return count('artist', match[1]);
    if ((match = source.match(/^(\d+) 个账号已连接$/))) return `${count('account', match[1])} connected`;
    if ((match = source.match(/^(\d+) 首匹配歌曲$/))) return `${count('track', match[1])} matched`;
    if ((match = source.match(/^(.+) · (\d+) 首$/))) return `${translateCore(match[1])} · ${count('track', match[2])}`;
    if ((match = source.match(/^播放专辑 (.+)$/))) return `Play album ${match[1]}`;
    if ((match = source.match(/^播放艺术家 (.+)$/))) return `Play artist ${match[1]}`;
    if ((match = source.match(/^播放 (.+)$/))) return `Play ${match[1]}`;
    if ((match = source.match(/^为 (.+) 选择头像$/))) return `Choose a picture for ${match[1]}`;
    if ((match = source.match(/^在新窗口播放 (.+) 的 MV$/))) return `Play the MV for ${match[1]} in a new window`;
    if ((match = source.match(/^在新窗口播放 (.+) 的视频$/))) return `Play the video for ${match[1]} in a new window`;
    if ((match = source.match(/^(\d+) 首 · 本页 (\d+) 个 MV$/))) return `${count('track', match[1])} · ${number(match[2])} MVs on this page`;
    if ((match = source.match(/^(\d+) 个音轨 · 仅播放声音$/))) return `${number(match[1])} audio tracks · audio only`;
    if ((match = source.match(/^已在新窗口打开 (.+) (MV|视频)$/))) return `Opened ${match[1]} ${match[2] === '视频' ? 'video' : 'MV'} in a new window`;
    if ((match = source.match(/^正在搜索 (.+)$/))) return `Searching ${match[1]}`;
    if ((match = source.match(/^(.+) 没有匹配歌曲$/))) return `No matching songs from ${match[1]}`;
    if ((match = source.match(/^(.+) 没有匹配视频音轨$/))) return `No matching audio tracks from ${match[1]}`;
    if ((match = source.match(/^(.+) 暂时限制了搜索$/))) return `${match[1]} has temporarily limited search`;
    if ((match = source.match(/^约 (\d+) 秒后可重试$/))) return `Try again in about ${match[1]} seconds`;
    if ((match = source.match(/^重新登录 (.+)$/))) return `Sign in to ${match[1]} again`;
    if ((match = source.match(/^(.+) 账号需要重新登录$/))) return `${match[1]} needs you to sign in again`;
    if ((match = source.match(/^(.+) 个人歌单暂不可用$/))) return `${match[1]} personal playlists are unavailable`;
    if ((match = source.match(/^(.+) 收藏夹暂不可用$/))) return `${match[1]} favorites are unavailable`;
    if ((match = source.match(/^打开 (.+)，(\d+) 首$/))) return `Open ${match[1]}, ${count('track', match[2])}`;
    if ((match = source.match(/^打开 (.+)，(\d+) (?:track|tracks)$/))) return `Open ${match[1]}, ${count('track', match[2])}`;
    if ((match = source.match(/^已连接 · (.+)$/))) return `Connected · ${match[1]}`;
    if ((match = source.match(/^连接 (.+)$/))) return `Connect ${match[1]}`;
    if ((match = source.match(/^(.+) · 与本地音乐完全分离$/))) return `${translateCore(match[1])} · kept separate from local music`;
    if ((match = source.match(/^没有可显示的(.+)$/))) return `No ${translateCore(match[1]).toLowerCase()} to display`;
    if ((match = source.match(/^登录后可在这里浏览(.+)，内容不会写入本地曲库。$/))) return `Sign in to browse ${translateCore(match[1]).toLowerCase()} here. Nothing is added to your local library.`;
    if ((match = source.match(/^(\d+) (?:track|tracks) · 由你创建$/))) return `${count('track', match[1])} · created by you`;
    if ((match = source.match(/^正在读取 (.+) (云歌单|个人歌单|收藏夹)$/))) return `Loading ${match[1]} ${translateCore(match[2]).toLowerCase()}`;
    if ((match = source.match(/^无法读取\s*(.+?)(云歌单|个人歌单|收藏夹)(。?)$/))) return `Could not load ${match[1].trim()} ${translateCore(match[2]).toLowerCase()}${match[3] ? '.' : ''}`;
    if ((match = source.match(/^刷新(云歌单|个人歌单|收藏夹)$/))) return `Refresh ${translateCore(match[1]).toLowerCase()}`;
    if ((match = source.match(/^已从音乐库移除“(.+)”（原文件不会被删除）$/))) return `Removed “${match[1]}” from the library (the original file was not deleted)`;
    if ((match = source.match(/^已更新“(.+)”的本地头像$/))) return `Updated the local picture for “${match[1]}”`;
    if ((match = source.match(/^当前设备：(.+)$/))) return `Current device: ${match[1]}`;
    if ((match = source.match(/^已降至 (.+)$/))) return `Fell back to ${match[1]}`;
    if ((match = source.match(/^当前在线音质：(.+)$/))) return `Current online quality: ${match[1]}`;
    if ((match = source.match(/^剩余 (\d+):(\d+)，届时只隐藏窗口$/))) return `${match[1]}:${match[2]} remaining; the window will be hidden`;
    if ((match = source.match(/^选择 (#[0-9A-Fa-f]{6})$/))) return `Choose ${match[1]}`;
    if ((match = source.match(/^(.+)，当前 (#[0-9A-Fa-f]{6})$/))) return `${translateCore(match[1])}, current ${match[2]}`;
    return source;
  }

  function translateCore(source) {
    if (!source) return source;
    if (resolvedLanguage === 'zh-CN') return source;
    return englishSourceCatalog[source] || dynamicEnglish(source);
  }

  function t(keyOrSource, args = {}) {
    const catalogValue = namedCatalog[resolvedLanguage]?.[keyOrSource];
    return interpolate(catalogValue || translateCore(String(keyOrSource)), args);
  }

  const protectedContentSelector = [
    '[data-i18n-skip]', '.lyric-line', '.queue-text', '.track-summary-text',
    '.media-parts strong', '.saved-playlist-tabs [data-id]', '.saved-playlists-page h2',
    '.saved-track-play strong', '.saved-track-play small', '#danmakuLayer',
    '.immersive-track-info', '.record-heading h2', '.record-heading p',
    '.track-title-cell .title-stack > strong', '.track-row > .track-cell',
    '.media-card > strong', '.media-card > span', '.search-artist-result strong', '.artist-card > strong',
    '.online-collection-header h1',
    '.online-collection-header p', '.music-folder-copy strong', '.music-folder-copy small'
  ].join(',');

  function shouldProtect(element) {
    return !!element?.closest?.(protectedContentSelector);
  }

  function translateTextNode(node) {
    if (!node?.parentElement || shouldProtect(node.parentElement)) return;
    const current = node.nodeValue || '';
    const previous = textSources.get(node);
    const source = !previous || (current !== previous.source && current !== previous.rendered)
      ? current
      : previous.source;
    const match = source.match(/^(\s*)([\s\S]*?)(\s*)$/);
    if (!match || !match[2]) return;
    const translated = translateCore(match[2]);
    const rendered = `${match[1]}${translated}${match[3]}`;
    textSources.set(node, { source, rendered });
    if (current !== rendered) node.nodeValue = rendered;
  }

  function translateAttributes(element) {
    if (!(element instanceof Element) || shouldProtect(element)) return;
    let sources = attributeSources.get(element);
    if (!sources) {
      sources = new Map();
      attributeSources.set(element, sources);
    }
    for (const name of ['aria-label', 'title', 'placeholder']) {
      if (name === 'title' && element.matches('[data-online-playlist-handle]')) continue;
      const current = element.getAttribute(name);
      if (!current) continue;
      const previous = sources.get(name);
      const source = !previous || (current !== previous.source && current !== previous.rendered)
        ? current
        : previous.source;
      const rendered = translateCore(source);
      sources.set(name, { source, rendered });
      if (current !== rendered) element.setAttribute(name, rendered);
    }
  }

  function localize(root = document) {
    if (!root) return;
    applying = true;
    try {
      if (root.nodeType === Node.TEXT_NODE) {
        translateTextNode(root);
        return;
      }
      if (root.nodeType === Node.ELEMENT_NODE) translateAttributes(root);
      const ownerDocument = root.ownerDocument || document;
      const walker = ownerDocument.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
      let current = walker.nextNode();
      while (current) {
        if (current.nodeType === Node.TEXT_NODE) translateTextNode(current);
        else translateAttributes(current);
        current = walker.nextNode();
      }
    } finally {
      applying = false;
    }
  }

  function flushPendingRoots() {
    scheduled = false;
    if (applying) {
      pendingRoots.clear();
      return;
    }
    const roots = [...pendingRoots];
    pendingRoots.clear();
    roots.forEach(localize);
  }

  function schedule(root) {
    if (!root) return;
    pendingRoots.add(root.nodeType === Node.TEXT_NODE ? root.parentElement : root);
    if (scheduled) return;
    scheduled = true;
    queueMicrotask(flushPendingRoots);
  }

  const observer = new MutationObserver(records => {
    if (applying) return;
    for (const record of records) {
      if (record.type === 'characterData') schedule(record.target);
      else if (record.type === 'attributes') schedule(record.target);
      else record.addedNodes.forEach(schedule);
    }
  });

  function updateDocumentLanguage() {
    document.documentElement.lang = resolvedLanguage;
    document.documentElement.dir = 'ltr';
    document.documentElement.dataset.language = resolvedLanguage;
    document.documentElement.dataset.languagePreference = preference;
  }

  function setPreference(value, { resolved } = {}) {
    const nextPreference = normalizePreference(value);
    const nextResolved = normalizeResolvedLanguage(resolved || (nextPreference === 'system' ? resolveSystemLanguage() : nextPreference));
    const changed = nextPreference !== preference || nextResolved !== resolvedLanguage;
    preference = nextPreference;
    resolvedLanguage = nextResolved;
    localStorage.setItem(STORAGE_KEY, preference);
    updateDocumentLanguage();
    if (changed) localize(document);
    return changed;
  }

  function applyAuthoritativeState(payload = {}) {
    return setPreference(payload.preference, {
      resolved: payload.resolvedLanguage
    });
  }

  updateDocumentLanguage();
  localize(document);
  observer.observe(document.documentElement, {
    subtree: true,
    childList: true,
    characterData: true,
    attributes: true,
    attributeFilter: ['aria-label', 'title', 'placeholder']
  });

  window.AuralisI18n = Object.freeze({
    get preference() { return preference; },
    get language() { return resolvedLanguage; },
    t,
    number,
    count,
    compare(left, right) {
      return String(left || '').localeCompare(String(right || ''), resolvedLanguage, { sensitivity: 'base', numeric: true });
    },
    lower(value) {
      return String(value || '').toLocaleLowerCase(resolvedLanguage);
    },
    localize,
    setPreference,
    applyAuthoritativeState
  });
})();
