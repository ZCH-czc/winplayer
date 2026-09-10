# Native ↔ WebView2 消息协议

2026-09-08：setPlatformConfiguration的能力投影也约束全屏扩展生命周期。丢失Comments/MediaExtras时用已有cancelPlatformExtras
取消对应等待、关闭对应面板并拒绝迟到回复；丢失VideoResolution或必填配置时，用已有setEmbeddedVideo(enabled:false)
取消准备中的切换。已生效的播放租约不因Web配置变化而停止；新评论/视频请求仍需当前能力。没有新增平台专属消息或公开媒体地址。

2026-09-08 插件兼容提示：inventory增加 state=`upgradeRequired`、compatibilityIssue=`manifestUpgradeRequired`；
导入/启用也可返回同一error。Web显示本地化升级说明，不显示内部路径。旧enabled偏好可关闭但不能重新启用；
CanEnable=false不是必须禁用“关闭”操作。消息action和原导入批准/重启协议不变，不能在Web自行改写清单或授权。

2026-09-08 歌词解耦：requestLyrics字段不变，仍由本地ID、sourceSelection、allowOnline、preference发起；不新增固定providerId。
Native完成本地/缓存/授权检查后按LyricsLookup能力路由，Web只展示返回的source/lines，不解析或重命名平台名称。相关资源缓存键20260908-lyrics-capability-r1。

### 2026-09-08 传输组件管理

`manageMediaTransportComponents {requestId,operation}` → `setMediaTransportComponents {requestId,operation,inventory?,preview?,cancelled?,error?}`。
list/pick/confirm/cancel/enable/select 与播放组件管理相同的字段和同源检查；独立 token/生命周期，不能跨组件种类确认。
pick 使用原生单包 .auralis-transport.zip/ZIP 选择器，不接受 Web 提供路径；45 秒解析/操作预算不含选择器等待。
确认信任明确包含组件处理媒体地址/请求头，但 preview/inventory 只投影安全元数据、摘要、计数和固定状态，不返回媒体请求或凭据。
固定错误 transportManagementFailed/busy；list 只校验不激活，可与别的只读 list 并行，修改/导入/重启互斥于另外两种管理。
本次启动选择在 MediaTransportServices 首次组合时冻结，CurrentDescriptor 在首次传输之前仅代表选择而非已激活；卡片文案据此为“本次启动传输组件”。
新修订默认关闭，选择/启用只影响下次启动，close/离页释放预览；不改变现有播放会话。静态缓存键 20260908-transport-components-r1。

### 2026-09-08 播放组件管理

`managePlaybackComponents {requestId,operation}` → `setPlaybackComponents {requestId,operation,inventory?,preview?,cancelled?,error?}`。
operation 为 list/pick/confirm/cancel/enable/select；安全整数 requestId，并验证 app.auralis.local 同源。
pick 只通过原生单文件选择器读取 .auralis-playback.zip/ZIP，不接收 JSON 路径；45 秒解析预算不包含选择器等待。
preview 只含不透明 token、id/displayName/version、manifestSha256/archiveSha256、fileCount/payloadBytes/capabilities，绝无路径/入口类型/流/凭据。
confirm 额外要求该预览 token 与 trust:true；enable 要求 id/enabled；select 的 id 为已批准且启用的组件，null 表示随包默认。
list 只校验文件，不激活 DLL。inventory 包含 items（元数据/启用/可用/选中/错误）、selectedId、current（名称/版本/bundled/fellBack）、restartRequired/stateError。
错误均为固定 playbackManagementFailed/busy；回调匹配 requestId 和 operation，不乐观切换，失败/超时保留旧列表并允许刷新。
取消、离开设置页、原生关闭会释放待确认预览；设置只影响重启后组合，不切换已有会话。重启复用 restartForPlugins，管理忙/待确认时拒绝。
随包默认与同 ID 外置修订为独立注册；无选择时只用随包默认。创建前托管失败回退默认后，本进程不再重试失败候选。

2026-09-07 SDK：插件 preview 增加 `hostRequirements:{minimumHostSdkVersion,requiredFeatures}`（清单元数据，非运行句柄）。
批次 item.error 可为 hostSdkIncompatible / hostFeatureUnsupported / hostApiIncompatible / manifestSchemaUnsupported，
每项都是固定枚举，不回传异常、路径、URL 或账号。兼容项仍由 token/trust 确认，不允许 Web 修改兼容声明。
inventory item 增加 state=incompatible 和 compatibilityIssue（同上错误码）；canEnable=false，已有 enabled 偏好仍可显式停用。
Web 转义并本地化原因和声明，静态资源缓存键为 20260907-plugin-compatibility-r1。

2026-09-07：插件导入 preview / batch.items[].preview 增加 `credentialAliases:[{key,scope,legacyKey}]`。
这些是清单批准范围（非 Cookie/Token 值），最多 16 项；Web 转义、展示并在信任勾选中明确包含该访问范围。
confirmPluginImport 仍只回传不透明 token 与 trust:true，不能从 Web 传入地址或秘密修改原生暂存批准集合。
取消、失败后重新确认和默认关闭不变；无声明时不显示访问面板。静态资源缓存键更新为 20260907-plugin-credentials-r1。

2026-09-07 下一阶段：当前成套 Native/Web 资源撤下旧平台专用账号、歌单 action/callback 和隐藏页面。
仅使用 manageOnlineAccount、requestOnlineCollection、setPlatformConfiguration.providers、setOnlineCollection；
这不是插件 ABI 变更，既有插件包及账号存储不变。旧页面不能与新 Native 混装，发布时必须更新资源缓存键。
旧平台专用协议表已移除，本文的账号/歌单表只描述当前通用入口；旧缓存页面需正常重启以加载配套资源。

2026-09-06：平台插件分离保持现有 Web action 和 UI payload 兼容；具体登录捕获移到私人插件，
主窗口仅协调 IPlatformNativeLoginSession。成功事件与窗口关闭分离，迟到成功不能覆盖显式退出。
Cookie、Token、签名媒体 URL 仍不进入主 WebView。见 [PLUGIN_SEPARATION.md](PLUGIN_SEPARATION.md)。

## 插件状态（2026-09-06）

2026-09-07：搜索不再使用核心内置平台 ID 白名单，Native 按当前清单能力及必填设置核验。
没有 Authentication 的 PlaylistBrowse 插件可以返回公开歌单，authentication 为 null，不能伪造 signedin；
有 Authentication 的账号歌单仍需登录确认。详情先核对句柄归属，再按能力决定是否验证账号；认证失败不得返回旧详情缓存。
Web 仅对声明 Authentication 的来源显示账号管理/登录，公开来源显示刷新/配置提示；账号失效会清除通用详情并丢弃迟到响应。

- providers 每项追加 `settings:[{key,label,description,kind,value,required,choices}]` 与 `configured`。
  configured 只表达必填配置，不代表登录或网络可用。
  `saveOnlineProviderSetting({requestId,providerId,key,value})` → `setOnlineProviderSettingResult({requestId,providerId,key,error,settings,configured})`。
  成功返回 Native 实际保存值；此保存/回读过程不调用账号或平台网络。
  Web 等待同时受当前声明生命周期约束：provider/key 消失或类型/必填/选项变化即清除等待、草稿和15秒计时器，
  迟到结果不再应用；普通元数据刷新不取消有效请求。此行为不新增 Native 取消命令或承诺回滚已写入值。
  Native 重验路由与声明选项；旧 setQqMusicQuality/setPlatformGateway 不再写入。见 [PLUGIN_SETTINGS.md](PLUGIN_SETTINGS.md)。
- 平台 UI 使用 `setPlatformConfiguration({providers:[{id,name,capabilities,authentication,playlists,error}],...})`。
  列表只来源于当前受信任、已启用且未停用的 Host 路由；没有插件时 providers 为空，不自动调用四个平台账号。
  `manageOnlineAccount({providerId,operation:login|signout|refresh})` 与 `requestOnlineCollection({providerId,handle,requestId})`
  由 Native 核验当前路由，歌单回调 `setOnlineCollection` 同时匹配 providerId/handle/requestId。所有 URL 与会话仍留在后端。
- 停用现在撤销该插件的凭据读写（保留删除能力），拒绝其后续能力路由，调用受信任插件自己的 SignOut / ClearLoginData。
  管理结果新增 credentialsCleared:true/false；false 必须显示清理未完成及重试，不等同清理成功。启用仍需重启；停用不删除本地音乐或歌单。
- `restartForPlugins` 无路径、参数或可执行文件输入：仅启动当前 EXE 并传入当前 PID/启动时间，子进程核对同一路径，
  等待旧实例正常退出再进入单实例流程。管理未完成时拒绝重启。MSIX 下的交接仍需单独验收。

- 批量导入：`pickPluginPackage` 原生选择器支持多选，`dropPluginPackages` 使用相同正整数 requestId，
  只接收 `postMessageWithAdditionalObjects` 提供的 1–16 个真实 `CoreWebView2File`；Native 验证 app.auralis.local 来源，
  不接受 JSON 路径、URL、目录或虚构 File。两入口调用同一批次校验。每批压缩文件及暂存展开内容分别最多 256 MiB，单包仍限 128 MiB。
- 管理回调新增 `batch:{token,items:[{fileName,preview?,error?}]}`；fileName 仅为限长净化后的文件名，不含路径。
  preview 保留单包公开信息；同批相同插件 ID 全部标记 duplicatePlugin，非法包为 invalidPackage，批次超限为 batchLimit。
  `confirmPluginImport` 用批次 token 和 trust:true 确认全部有效项，确认前再校验哈希；回调 `results:[{fileName,id?,error?}]`，
  每项报告成功或失败，成功的新修订一次原子发布且全部关闭，不执行 DLL。确认失败时不能声称全部导入成功。
  取消清理整批未提交暂存；批次预览/确认各 45 秒，不累计为逐包 45 秒。拖入只进入设置预览，不自动信任、启用、播放或导航文件。

- Web → Native `requestPluginInventory({requestId})`：正安全整数；Native 同时最多一个检查，15 秒取消预算。
- Native → Web `setPluginInventory({requestId,items,issues,error?})`：item 只有 id/displayName/version/providers（显示名列表）/state/enabled/active/canEnable；
  state 为 enabled/disabled/enablePending/disablePending/untrusted/unavailable。enabled 是下次启动偏好，active 表示当前路由快照中已登记（不表示 DLL 已激活）。
  canEnable 只表示当前候选完整性允许启用，不代表已登录或媒体可播放；待启用/停用必须重启生效。
  issues 仅为枚举错误码，不下发目录、异常信息、入口类型或程序集路径。失败返回固定 inventoryUnavailable。
- Web 检查请求编号，20 秒无响应显示重试，刷新/失败保留旧列表；不在设置重绘时循环请求。
- Web → Native `openPluginFolder`：无参数，仅打开 Native 固定的 `%LOCALAPPDATA%/Auralis/Plugins`；不接受任意路径。
- 状态页不请求平台账号配置；清单发现和哈希检查不会激活插件、创建平台 HTTP client 或读取凭据。
- 管理 action 均带正安全整数 requestId：`pickPluginPackage` 打开原生多文件选择器，文件路径不进入 Web；
  `confirmPluginImport({token,trust:true})` 要求当前预览的 32 字符不透明 token 与显式信任；`cancelPluginImport` 丢弃当前未提交预览；
  `setPluginEnabled({id,enabled})` 验证插件 ID 和布尔值，只保存下次运行偏好。打开文件夹和刷新不触发这些修改。
- Native → Web `setPluginManagementResult({requestId,action,batch?,results?,cancelled?,error?})`：batch 内单包 preview 仅含 token/id/displayName/version/sha256/providers/capabilities；
  不含本机路径、账号或程序集入口。失败仅返回固定 pluginManagementFailed。Web 同时匹配 action 和 requestId，忽略迟到响应。
- 管理同时最多一项；原生选择器等待用户期间不计时，选择后准备包限 45 秒，其余管理操作同样限 45 秒。
  Web 非选择器操作 55 秒未确认时提示刷新，不声称回滚已提交结果。成功导入或开关后重新请求清单，不乐观修改开关状态。
- 新包/更新包默认关闭；启用和停用均不热变更当前 Host 快照。没有卸载、自动下载或热更新 action。

## 工作区新增：收藏歌曲信息恢复（2026-09-06，未打包）

- Web → Native `hydrateSavedTrack({handle})`：只接受 `saved-` 开头、最多 128 字符的本机句柄；从已保存条目解析平台标识，不接受外部 URL。
- Native → Web `setSavedTrackDetails({handle,item})`：item 为完整 `OnlineTrackView` 或 null；只包含公开展示信息与内部封面代理。
- Web 同时最多两路，可见行与播放入口触发；Native 两路限流、20 秒超时、失败冷却，不阻塞取流或写回用户歌单。
- 回调按 handle 合并到仍存在的条目/队列，原位补图；当前曲目补图不清歌词、不复位进度。删除后不重新插回列表。
- `setPlatformPlaybackResult` 成功时先读取既有 `item` 字段补全待提交条目，再切换播放状态。

## 工作区新增：运行实例与材质（未发布）

- Native → Web `setRuntimeBuild(string)`：版本 + 构建摘要（包含 Web 资源）；无安装路径、账号或凭据。
- Web → Native `setWindowMaterial({material:'mica'|'none'})`：只请求系统材质；Native 验证系统支持及高对比度。
- Native → Web `setWindowMaterialState({mica:boolean})`：只有 true 才允许 CSS 背景透明，不支持时保留实色。
- `setEmbeddedVideoState(enabled:true)` 播放中等待真实画面，暂停输入允许 MediaOpened 确认但保留待播放提示；
  20 秒无首帧退回音频。现有 handle/requestId/取消规则不变。`setEmbeddedVideoBounds` 现为空兼容入口。
- Native 内部 `https://video.auralis.local/{handle}/{requestId}` GET 只返回当前输入最新 JPEG；no-store，
  CORS 只允许 `http://app.auralis.local`，无 listener、不进入 LAN、不包含媒体 lease/headers。Web 单请求取帧并隔离迟到 bitmap。
- 重复应用启动走同用户命名管道，不走 Web bridge。负载上限 16 KiB、音频路径最多 4096 字符；
  接收方重新按 Shell 激活规则校验。只接受激活与本地文件，不允许任意命令、网络地址或秘密。

> 适用版本：0.16.8
> 实现端：`Auralis/MainWindow.xaml.cs` 与 `Auralis/wwwroot/app.js`

主 UI 是受信任本地页面，但它仍位于 WebView2 进程中。文件系统、播放器、凭据、网络 lease 与 Windows 能力
只能由 Native 持有；页面用消息表达用户意图，Native 用回调发布可信结果。本文是两端联动修改的检查表。

## 1. 传输方式

### 1.1 Web → Native

`app.js` 的 `nativePost(action, payload)` 最终发送：

```js
window.chrome.webview.postMessage({ action, ...payload });
```

`MainWindow.CoreWebView2.WebMessageReceived` 读取 `e.WebMessageAsJson`，检查顶层 `action`，再按字符串 switch。
消息是 fire-and-forget；需要结果的操作由 Native 稍后回调页面。

示例：

```json
{
  "action": "loadTrack",
  "id": "<local-track-id>",
  "seconds": 84.2,
  "autoplay": false
}
```

### 1.2 Native → Web

Native 对 JSON 使用 `System.Text.Json` 序列化，再执行受控脚本：

```csharp
await ExecuteScriptAsync($"window.Auralis?.setPlaybackState({payload})");
```

`app.js` 在启动末尾暴露 `window.Auralis`。回调可能在页面初始渲染后、异步操作完成时、文件监听事件或播放
心跳中发生。

### 1.3 当前协议缺少什么

0.16.6 仍没有统一的 envelope version、自动 JSON schema、通用 request/response promise 或生成式类型。契约靠两端
代码和本文同步，静默拼写错误是现实风险。新增领域时优先扩展成带 `requestId` 的独立 action/callback，不要依赖
全局变量猜测响应属于谁。

## 2. 信任与序列化规则

1. Web 提交的字符串、ID、URL、数值和设置都视为不可信输入。
2. Native 只能按本地曲库 ID 解析文件，不能播放页面提交的任意路径。
3. 在线页面只回传 Host 生成的不透明 handle（临时结果为随机值，本机收藏由本机 entry ID 派生），不能回传/接收真实 Provider opaque ID、URL、headers 或 Cookie。
4. `ExecuteScriptAsync` 的动态文本必须来自 JSON 序列化，不手工插入用户字符串。
5. Native 对数值做范围限制：音量 0..1、速度 0.5..2、托盘定时 0..1440 分钟、缓冲 100..5000 ms 等。
6. 异步列表请求必须带 request ID；Web 丢弃迟到、query/provider/handle 不匹配的响应。
7. 错误使用可展示的脱敏摘要；原始 HTTP body、异常栈、URL query 或凭据不能进入页面。
8. callback 在 WebView 已销毁、正在退出或导航未完成时应安全失败，不阻止资源释放。

## 3. Web → Native 动作总表

### 2026-09-05 媒体扩展（工作树）

`restartPlayback {id}` 只重开 Native 当前媒体（包括当前视频及独立音轨），从 0 播放；拒绝非当前 ID，
不以 ended 状态下的 seek + resume 实现循环。`prefetchPlatformTrack {currentId,handle?}` 仅准备当前队列的下一首，
空 handle 取消；Native 校验当前身份、已有曲目句柄，最多一个可取消任务，失败不影响当前播放。
预取不改变当前歌曲/媒体输出，短期 lease 和缓存路径不进入 Web。用户改队列/播放模式时替换预取。

评论条目新增 `avatarUrl`，只允许 Native 注册的 `https://platform-art.auralis.local/<opaque-handle>`，
不下发平台头像原始地址。评论右侧抽屉接近底部时按已有 `nextPageHandle` 串行追加；错误停止自动翻页并保留手动重试。
`nativeEnded(id?)` 携带结束时的本地 ID/在线 handle，Web 忽略非当前项的迟到结束事件。
在线播放成功先向 Web 提交新曲目的标题/封面/歌词等待态和视频退出，再开启解码；回调等待期间仍校验取消。

- `requestPlatformExtras {handle, requestId, kind:"parts"|"danmaku"|"comments", pageHandle?}` →
  `setPlatformExtras {handle,requestId,kind,items,nextPageHandle,error}`。仅用户打开面板/启用弹幕后请求；
  Native 持有真实实体 ID、评论 cursor，UI 按 requestId + handle + kind 丢弃迟到响应。分 P 返回新的播放句柄。
- `requestSavedPlaylists` / `createSavedPlaylist {name}` / `addToSavedPlaylist {playlistId,handle?,localId?}` /
  `removeFromSavedPlaylist {playlistId,entryId}` → `setSavedPlaylists {items,action,requestId?,error?}`。
  创建/添加带递增 `requestId`；Native 在成功和失败回调中原样返回。只有匹配 action + requestId 才完成当前表单，
  避免关闭、重开后旧请求误关新弹层；写入处理中禁用重复提交，异步刷新保留输入、焦点与滚动位置。
  在线实体只保存显式用户收藏的非敏感引用于独立 `saved-playlists.json`；不保存流、headers、Cookie、封面 URL 或临时句柄，
  不进入 library.json、最近、LAN 或平台账号歌单。不向平台执行收藏写入。
- `setEmbeddedVideo {handle,enabled,requestId}` → `setEmbeddedVideoState {handle,requestId,enabled,error}`。
  仅当前具有视频能力的歌曲、用户明确点击时解析视频；同一 Native 播放器在主窗口内显示画面，Web 不接收媒体地址。
  `setEmbeddedVideoBounds {x,y,width,height,visible}` 为旧缓存页面的空兼容入口；当前 canvas 由 Web 布局。
  退出全屏/切歌/取消必须撤销视频请求并恢复纯音频，不创建独立 MV 窗口。

播放模式由唯一 Web 状态决定，两处按钮共享顺序→列表循环→单曲循环→随机→顺序，SVG/标签同时更新。

`cancelPlatformExtras {kind?}` 不带 kind 时撤销全部读取；关闭评论或分 P 面板时只撤销该类请求，不打断弹幕。
响应匹配 kind/handle/requestId；评论 continuation 只在 Native 保存。
当前画面资源路径必须携带视频 `handle` / `requestId`，拒绝上一视频的迟到请求；画面可参与 Web 进退动画。
`setSavedPlaylists.items` 为 `{id,name,entries:[{entryId,track:<本地 TrackInfo 或 OnlineTrackView>}]}`，另有 `action` 区分刷新/创建/添加/移除。
写入只接受现有 handle 或本地 ID，不能由 Web 提交 Provider ID、文件路径或媒体 URL；失败保留原文件。
`saved-<entryId>` 缓存过期后可由 Native 从本机持久化引用恢复，不要求重新搜索。

### 3.1 窗口

| `action` | 负载 | Native 行为/回调 |
| --- | --- | --- |
| `windowDrag` | 无 | 发送原生标题栏拖动，保留 Snap |
| `windowToggleMaximize` | 无 | 最大化/还原；随后 `setWindowState` |
| `windowToggleFullscreen` | 无 | 进入/退出当前显示器 Native 全屏；`setFullscreenState` |
| `windowToggleTopmost` | 无 | 切换用户置顶；全屏仍临时置顶 |
| `windowMinimize` | 无 | 最小化主窗口 |
| `windowClose` | 无 | 触发 WPF Close；可能按设置转托盘 |
| `setUiLanguage` | `{ preference: "system"\|"zh-CN"\|"en-US" }` | Native 验证值、保存 `window-settings.json`、更新关键 Native 文本并回调 `setUiLanguageState` |
| `openApplicationLogs` | 无 | 请求 Explorer 打开 Native 已确定的应用日志目录；目录不可用/打开失败时显示 Native 本地化错误 |

`windowDrag` 只能由实际拖动区域的主鼠标按钮触发；按钮、输入框和 `data-no-drag` 子树不得冒泡触发。

`setUiLanguage` 没有 request ID，因为它是小型、串行的本地偏好写入。Web 不得提交任意 culture 名称；未知或缺失值
必须被 Native 拒绝并保持当前状态。`openApplicationLogs` 不携带路径或 shell 参数，防止 Web 借该 action 打开任意位置。

### 3.2 Windows 行为与设置

| `action` | 负载 | Native 行为/回调 |
| --- | --- | --- |
| `setCloseToTray` | `{ enabled: bool }` | 设置关闭到托盘；当前 Native 同步托盘启用并写 `window-settings.json` |
| `setTrayEnabled` | `{ enabled: bool }` | 显示/隐藏托盘；当前 Native 同步 close-to-tray |
| `setTrayMinimizeTimer` | `{ minutes: int }` | 0 取消，1..1440 启动；周期 `setTrayMinimizeTimerState` |
| `setStartupEnabled` | `{ enabled: bool }` | MSIX StartupTask 或 HKCU Run；失败用 toast |
| `setMediaKeys` | `{ enabled: bool }` | 控制窗口键盘路径；当前不完全禁止 SMTC 命令 |
| `openDefaultAppsSettings` | 无 | 打开 Windows 默认应用设置 |
| `themeChanged` | `{ theme: "light"|"dark" }` | 同步 Native/DWM/任务栏实际主题，不传 `system` |

主题用户意图 `system` 由 Web 解析成实际 light/dark 后发送。Windows 主题变化时若偏好为 system，需要重新发送。

### 3.3 音频设备

| `action` | 负载 | Native 行为/回调 |
| --- | --- | --- |
| `requestAudioDevices` | 无 | 枚举模块、设备并做 endpoint 诊断；`setAudioDevices` |
| `setAudioOutputSettings` | `{ settings, includeDiagnostics? }` | 应用设置并返回最新 `setAudioDevices` |

`settings` 当前形状：

```json
{
  "outputModule": "auto",
  "outputDeviceId": "",
  "channel": "stereo",
  "bufferMilliseconds": 1000
}
```

不要添加“独占/bit-perfect/DSD 直通”字段来暗示不存在的能力。输出格式、采样率行为和 DSD 行为是诊断说明，
不是可保证的硬件能力。

### 3.4 桌面歌词与任务栏

| `action` | 负载 | Native 行为/回调 |
| --- | --- | --- |
| `setDesktopLyricsOptions` | `{ options: DesktopLyricsOptions }` | 创建/更新/隐藏桌面歌词；dataset 与锁定回调 |
| `setTaskbarMediaOptions` | `TaskbarMediaOptions` 字段在顶层 | 更新任务栏紧凑条 |

桌面歌词 option：

```json
{
  "enabled": false,
  "fontSize": 30,
  "fontWeight": 600,
  "activeColor": "#73BCFC",
  "inactiveColor": "rgba(32,33,36,0.58)",
  "shadowColor": "rgba(255,255,255,0.72)",
  "maskColor": "rgba(255,255,255,0.36)",
  "maskBrightness": 36,
  "alignment": "center",
  "showMask": true,
  "animate": true,
  "wordHighlight": true,
  "showTranslation": true,
  "doubleLine": true,
  "alwaysShowSongInfo": true,
  "locked": false,
  "offsetMilliseconds": 0
}
```

注意：0.16.6 的 `MaskColor` 和真正逐词 `WordHighlight` 尚未完整进入 WPF 渲染，不要把字段存在当作功能已完成。

任务栏 option：

```json
{
  "action": "setTaskbarMediaOptions",
  "schemaVersion": 3,
  "enabled": false,
  "autoShowOnPlayback": false,
  "flyoutEnabled": false,
  "showOnTrackChange": false,
  "showControls": true,
  "theme": "light",
  "accent": "#6B9DCA"
}
```

当前 Web 明确把 `flyoutEnabled` 和 `showOnTrackChange` 固定为 false；对应弹窗类没有实例化。

### 3.5 本地曲库与图像

| `action` | 负载 | Native 行为/回调 |
| --- | --- | --- |
| `pickFiles` | 无 | 多选音频；`addTracks`/toast/scanning |
| `pickFolder` | 无 | 多选文件夹、扫描；`setMusicFolders` + 曲库增量 |
| `requestMusicFolders` | 无 | `setMusicFolders` |
| `removeMusicFolder` | `{ id }` | 移除监听根，不删磁盘歌曲；刷新文件夹 |
| `rescanMusicFolder` | `{ id? }` | 有 ID 扫一个，无 ID 扫全部；增量回调 |
| `removeTrack` | `{ id }` | 从曲库索引移除，不删除源文件；`removeLibraryTracks` |
| `pickArtistImage` | `{ artist }` | 复制到 Native 缓存；`setArtistImage` |
| `pickWindowBackground` | 无 | 复制本地图；`setWindowBackground` |

`removeMusicFolder.id` 是 Native 生成的文件夹标识；不能把 UI 显示路径当成任意删除路径。

艺术家封面高斯模糊是 Web-owned preference，键为 `auralis:artist-cover-blur`。它只改变 `artistAvatarMarkup`
的 CSS 状态，不需要新的 Native action，也不改写 `ArtistImages/` 中的源图。`pickArtistImage`/`setArtistImage` 契约保持不变。

### 3.6 歌词

局域网复制失败的恢复回调为 `window.Auralis.showLanPairingLink()`：不携带新的凭据字段，使用现有
`setLanMusicSharingState` 的 `pairingUrls` 展开当前完整链接。Native 先刷新共享状态再调用此方法；
只在局域网设置页呈现，不强制导航或向日志输出链接。

| `action` | 负载 | Native 行为/回调 |
| --- | --- | --- |
| `requestLyrics` | `{ id, allowOnline, preference, sourceSelection }` | 加载并 `setLyrics` |
| `pickLyricsFile` | `{ id }` | 为本地曲目选择 LRC；`setLyrics` + index |
| `resetLyricsOverride` | `{ id }` | 删除逐曲覆盖；重新加载 + index |
| `clearLyricsCache` | `{ id }` | 删除在线缓存；必要时重载 + index |
| `requestLyricsCacheIndex` | 无 | `setLyricsCacheIndex` |

`id` 仅为本地曲目 ID。在线播放歌词由 Native 在在线播放成功后按当前 handle 自动路由，不经过本地 cache action。
异步结果必须核对当前本地/在线身份，防止上一首歌词覆盖新歌。

### 3.7 本地播放

| `action` | 负载 | Native 行为/回调 |
| --- | --- | --- |
| `playTrack` | `{ id }` | 从头自动播放本地曲目 |
| `loadTrack` | `{ id, autoplay, seconds }` | 会话恢复/预载并定位 |
| `resumePlayback` | 无 | LibVLC play，启动心跳，状态回调 |
| `pausePlayback` | 无 | LibVLC pause，停止普通心跳，状态回调 |
| `seekPlayback` | `{ seconds }` | 非负定位；状态回调 |
| `setVolume` | `{ value }` | clamp 0..1 |
| `setPlaybackRate` | `{ value }` | clamp 0.5..2；状态回调 |

上一首、下一首、随机和循环决策在 JavaScript 队列中；Native 收到 EndReached 后调用 `nativeEnded()`，Web 再决定
下一项并发新的播放 action。

### 3.8 在线搜索与播放

| `action` | 负载 | Native 行为/回调 |
| --- | --- | --- |
| `platformSearchTracks` | `{ requestId:int, query, providerId, pageSize, pageHandle?:string|null }` | `setPlatformSearchResult` |
| `cancelPlatformSearch` | `{ requestId? }` | 当前实现取消正在进行的搜索；requestId 仅供页面记账 |
| `playPlatformResult` | `{ handle }` | 解析 lease/准备源/播放；`setPlatformPlaybackResult` |
| `openPlatformMusicVideo` | `{ handle }` | 仅有视频能力的结果；`setPlatformVideoResult` |
| `getPlatformConfiguration` | 无 | `setPlatformConfiguration` |
| `saveOnlineProviderSetting` | `{ requestId, providerId, key, value }` | 按已启用清单重验声明设置；`setOnlineProviderSettingResult` |

搜索 `pageSize` 会由契约限制到 1..100。第一页不传 `pageHandle`；成功回调中的 `nextPageHandle` 是 Native
为 Provider continuation/cursor 生成的随机内存句柄，页面只能原样带入相同 Provider、相同 query 的下一页请求。
Provider 原始 cursor、签名参数和会话材料不得进入 WebView。页面可缓存已经显示过的页，但不得持久化页句柄；
句柄约 2 小时后或进程重启后失效，届时应回到第一页重新搜索。播放结果 `handle` 同样是有界、短期的随机内存句柄。
`setPlatformSearchResult.error.retryAfterSeconds` 存在时，Web 必须保留当前页，在当前错误卡片显示冷却状态并禁用
重试按钮，时间到后再恢复；不得用前端循环请求绕过 Provider 限流。

### 3.9 平台账号与歌单

| `action` | 负载 | Native 行为/回调 |
| --- | --- | --- |
| `manageOnlineAccount` | `{ providerId, operation }` | `login` / `signout` / `refresh`；能力验证后执行，刷新配置 |
| `requestOnlineCollection` | `{ providerId, handle, requestId }` | 精确归属及能力核对；`setOnlineCollection` |

在线歌单详情必须同时核对 `requestId` 与 `handle`。登录失效时回调带认证状态/typed error，Web 清除详情缓存、同步
“在线歌单”页和设置，并在当前页面显示重新登录按钮。Cookie 与刷新令牌由原生插件和凭据存储处理，
不能出现在上述 action 负载中。

登录窗口的关闭不表示认证成功。具体账号验证和会话续期规则由插件实现，不在通用协议中规定平台API。
候选会话、多账号上下文和随机挑战仅在 Native/Plugin 内传递，验证后才提交正式凭据，任何失败保留原账号。
成功窗口保留“完成”按钮；失败保留窗口和具体错误、提供“重试连接”。主 WebView action/payload 不增加任何秘密字段。
凭据边界见 [`PLUGIN_CREDENTIALS.md`](PLUGIN_CREDENTIALS.md)。不能根据窗口跳转或头像出现自行推断认证成功。

### 3.10 局域网浏览器播放器

| `action` | 负载 | Native 行为/回调 |
| --- | --- | --- |
| `requestLanMusicSharingState` | 无 | 返回当前 `setLanMusicSharingState` 权威快照；不会隐式启用或绑定端口 |
| `setLanMusicSharingOptions` | `{ enabled:bool, port:int }` | 规范化并保存 `lan-sharing.json`，启停/重绑服务，随后回调状态 |
| `regenerateLanMusicSharingAccess` | 无 | 轮换约 10 分钟有效的一次性配对 token；已有 browser session 保留 |
| `revokeLanMusicSharingSessions` | 无 | 清除全部 browser session，并同时轮换配对 token |
| `copyLanMusicSharingUrl` | 无 | 复制首个可用配对 URL；只使用 Native 生成地址，不接受页面提交 URL |
| `openLanMusicSharingUrl` | 无 | 优先在默认浏览器打开 loopback 配对 URL，否则打开首个私有地址 |

`port` 只允许 1024..65535，未知/越界值回退默认 `43821`。`enabled` 默认 false；构造服务本身不能绑定端口。
配对 URL 包含短期 secret，因此只能在可信主 WebView 中显示/复制，不能进入日志、持久化文件或外部 analytics。
启停、绑定失败和网卡变化通过同一个状态回调反映，错误不能阻断本地桌面播放。

## 4. Native → Web 回调总表

### 4.1 曲库与资源

| `window.Auralis` 方法 | 形状 | 页面行为 |
| --- | --- | --- |
| `receiveLibrary(tracks)` | `TrackInfo[]` | 替换全部本地曲库 |
| `updateLibraryTracks(tracks)` | `TrackInfo[]` | 按 ID 增量 upsert |
| `removeLibraryTracks(ids)` | `string[]` | 移除曲目并清收藏/最近引用 |
| `setMusicFolders(folders)` | 文件夹 view[] | 更新设置页文件夹 |
| `playLocalTrack(id)` | string | Shell/外部激活转入 Web 队列 |
| `addTracks(tracks)` | `TrackInfo[]` | 增量添加、停止 scanning、toast |
| `setScanning(bool)` | bool | 扫描状态 |
| `setArtistImage(artist,url)` | string,string | 保存 UI 映射并重绘 |
| `setWindowBackground(url)` | string | 应用本地背景 |
| `showToast(message)` | string | 显示短提示；不能承载唯一错误恢复入口 |

`TrackInfo` JSON 形状（camelCase）：

```json
{
  "id": "...",
  "title": "...",
  "artist": "...",
  "album": "...",
  "fileName": "...",
  "extension": ".flac",
  "coverUrl": "https://covers.auralis.local/...",
  "size": 12345678,
  "durationSeconds": 240.5,
  "dateAdded": "2026-08-29T..."
}
```

页面不能从 `fileName` 推导/重建真实路径。

### 4.2 歌词

| 方法 | 形状 | 规则 |
| --- | --- | --- |
| `setLyrics(payload)` | `LyricsResponse` | 替换当前歌词与 availability |
| `setLyricsCacheIndex(rows)` | cache info[] | 设置缓存弹窗数据 |
| `nativeDesktopLyricsLockChanged(locked)` | bool | 同步 localStorage/控件与提示 |

`LyricsResponse` 包含 track ID、来源、local/online 标识、行、状态与 availability。新增字段必须保持旧页面对缺失值
有默认值；不要把完整歌词文件路径发给 Web。

### 4.3 在线

| 方法 | 关键字段 | 迟到响应规则 |
| --- | --- | --- |
| `setPlatformSearchResult(payload)` | `requestId, query, providerId, pageHandle, items, nextPageHandle, totalCount, error` | requestId、query、providerId 与 pageHandle 都必须匹配当前请求；迟到页不得覆盖当前页 |
| `setPlatformPlaybackResult(payload)` | `handle, success, quality, error` | handle 必须等于 pendingHandle |
| `setPlatformVideoResult(payload)` | `handle, success, error` | 同上 |
| `setPlatformConfiguration(payload)` | providers：动态能力、声明设置、账号/歌单状态 | 跨页面权威快照，移除不再可用的来源 |
| `setOnlineCollection(payload)` | `providerId,requestId,handle,detail,error` | providerId、requestId、handle匹配；认证错误清该来源详情缓存 |
| `setOnlineProviderSettingResult(payload)` | `providerId,requestId,key,error` | 只确认匹配的设置请求，失败保留编辑草稿 |

在线 track view 只含安全字段：`handle,id,kind,providerId,sourceName,title,artist,album,coverUrl,durationSeconds,
availability,isPlayable,hasMusicVideo`。这里的 `id` 仍是展示/归属信息；页面后续操作必须使用 `handle`。

质量摘要至少表达最终 label、bitrate 与是否 fallback。页面必须以 Native 成功回调的最终质量更新播放栏。

MV 是 Native 窗口，但音量不是独立状态。用户在 MV 窗口调节音量后，Native 调用
`setPlaybackVolume(value)` 更新主页面的两个音量滑块和持久化值；页面不得回发同一值形成消息循环。

### 4.4 窗口、DPI、计时与音频

| 方法 | 形状 | 页面行为 |
| --- | --- | --- |
| `setWindowState(maximized)` | bool | 切换还原/最大化图标 |
| `setPlaybackVolume(value)` | 0..1 | 接受 MV 窗口的音量变化，同步播放栏与 localStorage，不回发 Native |
| `setDpiScale(scale)` | number ≥1 | 设置 `data-dpi` 与 `--native-dpi-scale` |
| `setFullscreenState(fullscreen)` | bool | 同步全屏 layer class |
| `setTrayMinimizeTimerState(payload)` | `{active,remainingSeconds}` | 更新倒计时，不持久化 timer |
| `setAudioDevices(payload)` | 模块/设备/设置/endpointLatency | 更新音频二级设置页 |
| `setUiLanguageState(payload)` | `{ preference: "system"\|"zh-CN"\|"en-US", resolvedLanguage: "zh-CN"\|"en-US" }` | 以 Native 权威解析更新 Web i18n 目录、重渲染当前页/队列/设置和本地化可访问文本 |

endpoint latency 形状：

```json
{
  "endpointId": "...",
  "deviceName": "...",
  "isBluetooth": true,
  "estimatedLatencyMilliseconds": 180,
  "streamLatencyMilliseconds": 50,
  "enginePeriodMilliseconds": 10,
  "status": "estimated"
}
```

蓝牙值是系统可观测数据与估算，不是耳机端到端实测。UI 必须显示“估算/不可用”等状态。

`setUiLanguageState` 会在导航完成、WebView 重载后以及每次语言设置成功后发送。`preference` 表示用户选择，
`resolvedLanguage` 表示当前实际目录；当 preference 为 system 时，后者仍只能是 `zh-CN` 或 `en-US`。回调不包含
Windows 用户区域、完整 culture 名称、路径或任何用户内容。

### 4.5 播放

| 方法 | 形状 | 页面行为 |
| --- | --- | --- |
| `setPlaybackState(playback)` | `{id,isPlaying,currentTime,duration,audioInformation?}` | ID 与当前本地 ID 或在线 handle 匹配后更新 |
| `nativeEnded()` | 无 | Web 按 repeat/shuffle/queue 决定下一首 |
| `mediaCommand(command)` | `playPause|next|previous` | 转发 Windows/任务栏命令到 Web 队列 |

页面不得接受不匹配 ID 的旧播放状态。在线当前项中 Native 的 `id` 应与页面保存的 handle 对得上；如果改变这一
约定，必须同时修改过滤逻辑并加契约测试。

### 4.6 局域网浏览器播放器

| 方法 | 形状 | 页面行为 |
| --- | --- | --- |
| `setLanMusicSharingState(payload)` | 见下方 | 更新独立设置页，不改变主播放状态 |

```json
{
  "enabled": true,
  "running": true,
  "status": "running",
  "port": 43821,
  "baseUrls": ["http://192.168.1.20:43821/", "http://127.0.0.1:43821/"],
  "pairingUrls": ["http://192.168.1.20:43821/#access=<short-lived-token>"],
  "pairingExpiresAt": "2026-08-30T12:34:56Z",
  "trackCount": 120,
  "activeSessionCount": 1,
  "errorCode": null,
  "errorMessage": null
}
```

`status` 当前为 `running|error|stopped` 的展示摘要；真实条件仍以 `enabled`/`running` 为准。URL 只由 Native 从
实际绑定地址产生。页面可以显示 base URL，并只把 pairing URL 交给显式复制/打开操作；不得把其写进 localStorage。
网络地址变化可能使数组和 token 同时变化，并撤销旧 session。

## 5. 异步与竞态约定

### 5.1 搜索

页面每次有效 query/provider 变化递增 `requestId`，先取消旧搜索，再发送新请求。Native 取消旧 CTS，回调携带
原 requestId/query/provider。页面只有三者都等于当前状态才提交结果。

### 5.2 歌单详情

每个平台维护：

```text
pendingRequestId
pendingHandle
requestTimer
detail
detailsByHandle
```

切换歌单时先清旧 timer，生成新 requestId。回调匹配后才清 pending。成功详情可短期缓存；账号不再 signed-in
时必须清全部详情和 pending，不能继续显示陈旧账号内容。

### 5.3 播放与歌词

新播放开始时先取消旧在线准备/歌词请求，提交新的当前身份，再经历任何 `await`。回调前重新检查当前 ID/handle。
这样能保证本地 LRC、在线歌词、桌面歌词与任务栏不会被旧请求覆盖。

### 5.4 页面生命周期

`beforeunload` 取消搜索并保存本地会话。Native 退出不能假设该事件一定执行；关键清理必须仍在 C# Dispose/
Closing 中完成。WebView2 重载后 Native 需要重新发送权威快照，不能依赖页面保留内存状态。

## 6. 错误约定

在线 typed error code 来自 `PlatformErrorCode`，包括：

```text
InvalidRequest Unsupported NotFound AuthenticationRequired Forbidden
RegionRestricted SubscriptionRequired ContentUnavailable RateLimited
NetworkUnavailable Timeout ServiceUnavailable InvalidResponse
ConfigurationRequired Cancelled Conflict Unknown
```

Web 的 `platformErrorText` 把它们转换为用户可读中文。建议 payload：

```json
{
  "code": "AuthenticationRequired",
  "message": "QQ 音乐登录已失效，请重新登录",
  "isTransient": false,
  "retryAfterSeconds": null
}
```

`message` 必须由后端脱敏生成，不直接透传第三方正文。认证错误需要页面内主操作；网络瞬时错误可提供重试；
取消通常不 toast。

## 7. 局域网浏览器 HTTP 协议

该协议和主 WebView bridge 分开。它由 `LanMusicSharingService` 在用户显式启用后提供，当前版本号为 1：

| 方法与路径 | 认证 | 响应/规则 |
| --- | --- | --- |
| `GET /` | 无 | 固定 `wwwroot/lan/index.html`，`no-store` |
| `GET /lan-player.js` / `GET /lan-player.css` | 无 | 固定白名单静态资源；不能访问其他磁盘路径 |
| `POST /api/lan/v1/session` | 一次性 token | JSON `{ accessToken }`；成功设置 `AuralisLanSession` HttpOnly cookie |
| `GET /api/lan/v1/library` | session cookie | `{ version, generatedAt, trackCount, tracks[] }`；每项含标准 `contentType` |
| `GET|HEAD /api/lan/v1/tracks/{id}/audio` | session cookie | inline 本地音频，显式 `Accept-Ranges: bytes`；并发满返回 429 |
| `GET|HEAD /api/lan/v1/tracks/{id}/cover` | session cookie | 仅受控 Covers 缓存，最大 16 MiB |

配对 token 在 URL fragment `#access=...` 中，所以不会随 HTTP request target 或 Referrer 发给服务器；页面必须在
读取后立即 `history.replaceState` 清除地址栏，再 POST 换 session。配对 token 约 10 分钟有效、成功一次后立即
轮换；session 最长约 12 小时、只在服务进程内。Cookie 为 `HttpOnly`、`SameSite=Strict`、Path `/`；当前端点是
HTTP，不能把它误写成 `Secure`/TLS。

页面加载时若没有 fragment token，必须先带现有 cookie 请求 library；成功则复用 session，只有收到 401/403 才
显示重新配对。这样地址栏清除 token 后的刷新/重新打开仍可使用 12 小时 session，且不需要让 JavaScript 读取
HttpOnly Cookie。

`tracks[]` 只允许：

```json
{
  "id": "per-service-opaque-id",
  "title": "...",
  "artist": "...",
  "album": "...",
  "extension": ".flac",
  "size": 12345678,
  "durationSeconds": 240.5,
  "dateAdded": "2026-08-30T...",
  "contentType": "audio/flac",
  "audioUrl": "/api/lan/v1/tracks/<id>/audio",
  "coverUrl": "/api/lan/v1/tracks/<id>/cover"
}
```

禁止字段：`SourceId`、`SourcePath`、`fileName`、绝对路径、在线 handle/entity、stream/artwork lease、URL/headers、
Cookie、token、credential、gateway 配置和平台错误正文。`id` 以每次 server start 重新生成的随机 HMAC secret 派生，
不能跨启动持久化或推断路径。浏览器提交的 `{id}` 只能命中当前原子目录快照；未知/旧 ID 返回 404。

服务端只接受 loopback、RFC1918/APIPA（实现也识别 IPv6 ULA/link-local peer）的私有来源，并对 Host 做实际监听
地址匹配；session POST 在存在 Origin 时必须同源。响应含 CSP、`frame-ancestors 'none'`、`nosniff`、no-referrer 和
权限限制。不要为便利增加 CORS、`AnyIP`、目录浏览、公网端口映射或把 token 放 query。

浏览器播放器是独立播放事实源：HTML Audio、队列、搜索、shuffle/repeat/volume 都在该标签页，不向主 WebView
发送播放 action。`<audio playsinline>` 使用目录给出的 MIME 判断浏览器支持；网络中断只允许一次带新 query 的
同源重试，解码失败不重试。停止服务、撤销会话、显式退出和网络地址变化后的 rebind 都撤销已有 session。

## 8. 新增消息的标准流程

`setPlaybackState.audioInformation` 是可选的当前媒体观测，字段为
`{codec,bitrateKbps,sampleRateHz,bitsPerSample,channels,isLossless,isAverageBitrate}`，数值未知时为 null。
它不包含路径、地址或取流凭据。旧播放组件不实现可选观测接口时发送 null；UI 清除旧观测，仍可显示
插件声明的标称音质。观测只在播放 ID 匹配时生效，不允许上一首的码率覆盖下一首。
FLAC 平均编码码率在标签中用“≈”区分，采样率、位深及声道放在标签说明中；不能用 PCM 理论码率
冒充压缩文件码率，也不能把网络吞吐量作为音乐码率。

1. 在本文先定义 action、payload、callback、错误和 ownership。
2. 选择 request ID：任何可能并发/取消/切页的读取都必须有。
3. 在 Web 建立 loading/success/empty/error/stale 五态，不先清空可用旧数据。
4. 在 Native 验证所有输入、范围、ID ownership 和生命周期。
5. JSON 由序列化器生成；禁止拼接用户文本进脚本。
6. 在 Native 处理 switch 与 `window.Auralis` 同时实现，不提交半边协议。
7. 给 Auralis.Tests 或新的 bridge 契约测试加入：有效、缺字段、越界、取消、迟到、页面销毁。
8. 更新 `ARCHITECTURE.md`/专题文档和 `ROADMAP.md` 状态。

推荐未来统一 envelope（尚未实现，不要假装已存在）：

```json
{
  "version": 1,
  "action": "domain.operation",
  "requestId": "uuid-or-monotonic-id",
  "payload": {}
}
```

在迁移前必须兼容当前扁平 action，不能一次性改名导致旧 Web/Native 混合部署失效。

## 9. 协议审查清单

- [ ] 页面没有收到文件绝对路径、stream URL、headers、Cookie、token 或 credential。
- [ ] Native 不信任页面提交的路径、URL、ID、颜色、数字或 enum。
- [ ] 异步响应有 request ID/handle/current identity 校验。
- [ ] 旧请求可取消，取消不显示错误 toast。
- [ ] Callback JSON 使用统一 camelCase 序列化。
- [ ] Web 对缺失/新增字段有默认值，Native 对缺失字段有安全默认。
- [ ] WebView 销毁、重载与应用退出期间 callback 安全。
- [ ] 两端实现、本文与自动测试同提交更新。
- [ ] LAN API 仍是默认关闭、仅本地库、私有地址和固定静态资源；没有绝对路径或在线平台材料。
- [ ] pairing token 未进日志/持久化/query，交换后从地址栏清除；stop/exit/rebind/revoke 清 session。
