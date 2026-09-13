# 插件与 Host SDK 兼容性（schema 5）

当前开发 SDK **2.10.0 / Abstractions 1.11.0**：Pages v6 增加归一化作品卡片。
documentVersion:6 必须声明 SDK 2.10 与 declarative-pages.v1 至 v6；旧 SDK 2.9 加载前拒绝。
平台需声明 StreamResolution，页面 action 仍只读；API/schema/程序集身份保持。
Bilibili 开发插件 1.18.0、QQ 1.16.0 采用 v6，均要求 SDK 2.10；网关 1.12.0 不变。
QQ 1.16 在 1.15 的目录页面上增加独立发现与音乐人搜索，只更新插件，未增加 SDK 版本或公开核心运行功能。
固定宿主兼容性通过不代表线上可用性；QQ 搜索最终匿名复验的 ServiceUnavailable 仍记为未通过。
旧签名安装版尚未更新；不要仅导入新包或手改清单冒充兼容。详见 PLUGIN_PAGES.md。

历史第六阶段 SDK **2.9.0 / Abstractions 1.10.0**：第六阶段新增 Pages v5 类型化实体导航。
源入口 documentVersion:5 必须声明 SDK 2.9 及 declarative-pages.v1/v2/v3/v4/v5；旧 SDK 2.8 在加载前拒绝。
API 1 / schema 5 / 程序集身份不变。Bilibili 开发包 1.17.0 采用此契约；QQ 1.14.0、网关 1.12.0 不变。
不能把新包导入旧 SDK 2.2 签名安装版；本轮未改动已安装应用。行为与验证见 PLUGIN_PAGES.md。

第五阶段 SDK **2.8.0 / Abstractions 1.9.0**：第五阶段新增 settings.v2。
分组、英文说明或 when 条件必须要求 SDK 2.8 与 settings.v1/v2；旧字段仍兼容，API/schema/程序集身份不变。
原生 scoped store 与 UI 共用声明和原子条件快照。插件业务读取不到暂不适用的配置。
QQ 私人插件 1.14.0、网关 1.12.0 采用此契约；Bilibili 1.16.0 保持 SDK 2.7 下限。
当前签名安装包不因此自动升级；详见 [设置契约与证据](PLUGIN_SETTINGS.md)。

第四阶段 SDK **2.7.0 / Abstractions 1.9.0**：第四阶段新增 Pages v4 的只读查询/选项筛选。
documentVersion:4 要求 declarative-pages.v1/v2/v3/v4；全局页另需 GlobalPages/global-pages.v1。
旧 SDK 2.6 拒绝此包；API 1、schema 5 与程序集身份不变。输入仅可通过宿主签发的表单句柄提交，
旧 Pages v1/v2/v3 和既有构造器仍兼容；新控件词汇依旧可能需要基础宿主更新，不能宣称永远无需更新。

历史开发 SDK **2.6.0 / Abstractions 1.8.0**：第三阶段新增 GlobalPages / global-pages.v1，
以及 documentVersion:3 / declarative-pages.v3 标签分区。全局入口必须声明 Pages + GlobalPages、
presentation:page、documentVersion:3 和 v1/v2/v3 feature；旧 SDK 2.5 会拒绝新包。
程序集身份和既有 API 构造器不变；不能仅修改 manifest 使旧 DLL 冒充实现了新能力。

历史 SDK **2.5.0 / Abstractions 1.7.0**。Pages 第二阶段新增 declarative-pages.v2；
入口 documentVersion 为 2 时必须要求该 feature。presentation:page / acceptsCreatorContext 也必须声明版本 2。
版本 1 默认 dialog，仍可使用；返回文档版本不能超过入口声明版本。API 1、schema 5 与程序集身份不变。
已经签名的 SDK 2.2 安装包不能导入本轮新插件；需要新版基础宿主后，契约内页面才能仅更新插件。

2026-09-13 当前开发 SDK **2.4.0 / Abstractions 1.6.0**：增加 `CreatorSearch` / `creator-search.v1`。
新增枚举追加到末尾，不改变既有编号/程序集身份/API。声明该能力必须显式要求其 host feature。
旧 SDK 2.2/2.3 不支持作者搜索插件的新版本；不要仅修改清单绕过检查。本轮没有发布新安装包。

## 页面契约引入：Host SDK 2.3.0 / 自检工具 1.2.0

2.3.0 新增 declarative-pages.v1（Pages），Abstractions 1.5.0，程序集身份和 API 1 不变。
清单仍为 schema 5；新增 pages 必须与 Pages 能力及 requiredFeatures 成套声明。旧已签名 0.16.11 包的 SDK 2.2 不支持此能力。
见 [声明式页面](PLUGIN_PAGES.md)。下方 2.0/1.2 为历史特性记录。

2026-09-08：公共 Host 的隐式平台凭据表与评论图片域名表已移除。当前播放器运行/导入最低 schema 为 5；
现有 schema 5 私人六包 1.11.0 的最低 SDK 1.2.0 仍满足，无需仅为 Host 2.0 修改接口、DLL 或最低版本。
下面的 1.2.0 记录和示例表示特性引入版本，不是当前 SDK 版本。

旧 schema 1–4 已安装包仍可离线识别，管理页显示“需要升级插件”，不能启用；导入返回 `manifestUpgradeRequired`。
原启用偏好、不可变包、收藏、设置和账号数据保留。用户需导入同 ID 完整新版包，确认声明授权，再启用并重启；不自动信任或迁移凭据。
关闭旧包时若无法依据声明确认账号清理，报告清理未完成而非按硬编码平台地址删除。新版按批准的精确声明清理。

Catalog 仍可静态检查旧格式，通用 Host 的可配置最低版本默认 1 仅供受控嵌入/历史测试；Auralis 生产及清理 Host 显式为 5。
ContractCheck `--inspect` 旧包可返回 Passed=true，但同时 `UpgradeRequiredPlugins>0`、`RuntimeManifestCompatible=false`；
`--verify` 在运行 DLL 前以 ManifestUpgradeRequired 拒绝。运行清单兼容不代表批准、账号、业务或原生播放已通过。
主 SDK major 升级来自移除公共兼容类及导入默认策略，API 1 / Abstractions 1.2.0 不变。

## 历史记录

2026-09-08：Host SDK1.2.0/schema5增加评论头像/表情域名声明；私人六包1.11.0，自检工具1.1.0。
旧schema1-4保持原评论图片兼容规则；该旧表仍含平台域名，是公开核心去硬编码目标的未完成项，不是最终架构。

2026-09-07：Host SDK 1.1.0 增加清单最低 SDK 与必需特性检查。播放器产品版本仍为 0.16.8；
Abstractions 包保持 1.2.0、API 1、程序集身份 1.1.0.0。Host 包版本与播放器产品版本不是同一版本号。
本轮不增加或修改 provider 契约接口，也不修改平台登录/取流逻辑。

## 四种不同的版本

| 字段 | 用途 |
| --- | --- |
| `schemaVersion` | 宿主能否理解这份 JSON；新格式为 5 |
| `minimumHostApiVersion` / `maximumHostApiVersion` | provider 契约的 API 大版本范围，当前为 1 |
| `hostRequirements.minimumHostSdkVersion` | 插件所需的 Host SDK 最低版本；当前 Host 2.0.0，schema5特性在1.2.0引入 |
| `version` | 插件包自身版本，与播放器和Host版本独立 |

不要用播放器 0.16.8 或 DLL 固定 AssemblyVersion 填写最低 Host SDK。接口程序集为兼容旧包保留身份，
所以仅看 AssemblyVersion 无法识别小版本新增能力。

## 清单示例

```json
{
  "schemaVersion": 5,
  "id": "example.music",
  "displayName": "Example Music",
  "version": "1.0.0",
  "minimumHostApiVersion": 1,
  "maximumHostApiVersion": 1,
  "hostRequirements": {
    "minimumHostSdkVersion": "1.2.0",
    "requiredFeatures": ["track-details.v1", "video-lease.v2", "comment-artwork.v1"]
  },
  "entryAssembly": "Example.Plugin.dll",
  "entryType": "Example.Plugin.Entry",
  "providers": [{
    "id": "example",
    "displayName": "Example",
    "commentArtworkDomains": [],
    "capabilities": ["TrackSearch", "TrackDetails", "VideoResolution"]
  }]
}
```

schema 4/5 必须提供 hostRequirements；最低 SDK 是规范的三段数字版本（如 1.2.0），拒绝前导零、
空白、预发布后缀、四段 AssemblyVersion、URL 和超长值。requiredFeatures 可为空，最多 32 项；
仅接受小写 ASCII 字母、数字、点和横线，以字母开头，长度不超过 64，拒绝重复值和通配符。
大小写精确匹配，不把未知特性默认为已支持。

## 当前特性集合

| 特性 | 何时必须声明 |
| --- | --- |
| `settings.v1` | 任一 provider 声明 settings |
| `credential-aliases.v1` | 清单声明非空 credentialAliases |
| `native-login.v1` | 声明 NativeLogin |
| `track-details.v1` | 声明 TrackDetails |
| `lyrics-lookup.v1` | 声明 LyricsLookup |
| `video-lease.v2` | 声明 VideoResolution，使用当前 transport/备用 URL 租约语义 |
| `comment-artwork.v1` | 所有schema5清单；每provider必须声明commentArtworkDomains，可为空 |

## 评论图片目的地

schema5的每个provider必须提供`commentArtworkDomains`数组：空数组拒绝全部，最多16项唯一小写ASCII DNS域名。
域名至少两段、总长不超过253、每段1–63字符；禁止URL、通配符、IP、端口、用户信息、路径、查询、末尾点、localhost/local后缀。
国际化域名可显式使用ASCII punycode；这不是公共后缀列表校验或DNS重绑定防护。安装进程内插件依然要求用户信任。
允许该域及其子域的标准HTTPS请求；首次请求、每次重定向及完整读取后重新授权。其他provider的声明不能借用。
声明只用于评论头像/表情，不隐式改变已有歌曲封面、平台API或媒体租约权限；也不授予任意代码网络沙箱权限。
schema1-4不接受该字段，静态读取时策略为deny-all，当前播放器不启用；自检的`LegacyCommentArtworkProviders`只表示旧格式计数，不授予域名。
插件只声明实际需要的评论图片域；没有这项需要的来源给出空数组。
关闭/禁用使句柄授权失效；已交给WebView的缓存不追溯删除，也不保证立刻停止全部下载字节。

新格式中遗漏对应必需声明视为无效清单，不能只写一个很低的 SDK 版本绕过检查。
普通 TrackSearch、Lyrics 等原 API 1 基础能力仍由 API 范围检查。未来增加不兼容语义时应使用新的
特性版本，而非改变同名特性的含义；增加契约接口时仍需同步接口包版本和测试。

## 发现、导入与降级

1. 只读取有限 JSON 元数据，先识别清单版本，再严格解析支持的字段。
   未来 schema 带有未知字段时也能返回明确的“不支持清单版本”，不会执行探测代码。
2. SDK 过低、缺失特性、API 不匹配和 schema 不支持分别产生有限诊断码。
   不加载 DLL、不创建 context、不访问凭据或网络；不注册其 provider 路由。
3. 批量导入按文件展示错误；不兼容项不安装，其他有效项仍可由用户确认导入。全批均不兼容时不能确认。
   兼容包的详情显示最低 SDK 和必需特性，原凭据访问确认、完整性记录、默认关闭保持不变。
4. 已安装包在较旧 SDK 中显示「与播放器不兼容」及原因，禁止启用；保留已有启用偏好和不可变安装目录，
   可显式停用。回到兼容的宿主后重新识别，不删除或重新迁移账号。
5. 停用无法解析的插件时不会绕过不兼容检查执行其退出/清理代码；可能报告登录清理未完成。
   恢复兼容且可信的宿主/包后再重试，不能把“已阻止路由”当作“Cookie 已删除”。

生产路径 Catalog、Host 和 PluginManager 共用 PlatformHostCompatibility.Current；嵌入宿主或隔离测试
可传入不可变的版本/特性快照。不要对 UI 开放修改版本或伪造支持特性的开关。

## 旧包与限制

- schema 1/2/3/4 仅保留离线识别，不替旧格式虚构requiredFeatures，不修改包、摘要或用户偏好；当前运行与导入要求schema5。
- 新包需与声明兼容的宿主搭配。此前宿主会拒绝未知清单，可能只显示通用错误。
- 核心无平台凭据/图片兼容表；凭据只能通过批准的声明访问，见PLUGIN_CREDENTIALS.md。
- 这些检查验证的是插件**声明的要求**，不是证明 DLL 没有缺失依赖、没有错误或没有谎报能力。
  激活后仍检查 descriptor、provider 接口并隔离运行错误。不能在发现阶段加载程序集来“提前证明”兼容。
- 版本通过不等于账号有效、平台服务在线、媒体可播放，也不是签名认证或恶意代码沙箱。

## 验证入口

`HostCompatibilityTests` 使用旧 SDK、缺失特性和旧 schema 的宿主快照，覆盖严格字段、接口漏报、
未知特性、未来 schema/API、惰性拒绝、真实夹具 DLL 激活、混合批次与降级/恢复偏好。
`Test-PluginSettings.cjs` 检查兼容原因、禁用状态、SDK 详情转义、中英文、键盘及六组主题/缩放。
私人六包用真实 Host 运行独立回归，但 HTTP、账号和媒体响应是合成夹具，不代表真实平台可用性。
