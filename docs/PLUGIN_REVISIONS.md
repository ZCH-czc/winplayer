# 单插件构建与宿主兼容矩阵

第十一阶段把“只更新插件”的验证变成一个构建入口：选择一个受信任的插件源码项目，
使用现成的发布 SDK DLL 编译，对比旧包，再进入独立交付门禁。公共工具不包含具体平台实现，
不依赖私人仓库；平台专用测试配置仍由各自插件仓库维护。

本阶段不改播放器、Host、Abstractions 或 Web 资源，不更新已安装的程序，不自动发布。
它不是更新服务器、安装器、自动回滚器，也不意味着任何新系统能力永远不需要基础宿主升级。

## 单插件入口

从源码仓库根目录运行 PowerShell 7.2+。需要 .NET 8、已准备好的项目依赖，UI 配置另需
仓库锁定的 Playwright 与 Edge。`FrozenCore` 必须是你信任的实际发布目录。

```powershell
./tools/Build-PluginRevision.ps1 `
  -Project C:/PluginSource/Example/Example.csproj `
  -FrozenCore artifacts/frozen-player `
  -PreviousPackage artifacts/previous/example.plugin-1.0.0.auralis-plugin `
  -PreviousDirectory artifacts/previous/platforms/example.plugin `
  -CompatibilityHosts @('artifacts/older-player') `
  -OutputDirectory artifacts/example-revision-check
```

以上为占位路径，需要替换为实际项目、旧包和发布目录。输出目录必须尚不存在；工具保留失败证据，
重跑使用新目录。旧 ZIP 与展开的单插件目录必须内容、清单和哈希精确一致。

项目应在 `FrozenCore` 模式下将 SDK 项目引用替换为发布 DLL 引用：

```xml
<ProjectReference Include="../Auralis.Platform.Abstractions/Auralis.Platform.Abstractions.csproj"
                  Condition="'$(FrozenCore)' == ''" />
<Reference Include="Auralis.Platform.Abstractions" Condition="'$(FrozenCore)' != ''">
  <HintPath>$(FrozenCore)/Auralis.Platform.Abstractions.dll</HintPath>
</Reference>
```

工具先求值项目，要求 **ProjectReference 为零**，SDK 引用路径精确指向所选发布目录。
随后只对该项目运行 `build --no-restore -p:BuildProjectReferences=false`。不自动恢复项目依赖，
不调用整个解决方案或全平台打包脚本；依赖未准备好时明确失败。
记录编译源文件、项目与清单哈希；这不是所有 MSBuild 导入/分析器的可复现构建证明。
源码项目和自定义 MSBuild 目标必须事先审阅，受信任的构建不是沙箱，不能将不可信项目交给它执行。

当前入口仅支持同 ID、严格递增版本、平铺的单 DLL 托管插件，输出三文件包：清单、插件 DLL、
其 deps.json，以及包外的 SHA-256 文件。不会把宿主 SDK、调试符号、开发批准凭据或账号目录装入插件包。
复杂依赖包暂不支持；不要通过追加任意构建输出绕过此约束。
插件仍可能使用宿主已提供的公共运行时依赖（例如桌面 WebView2）；该入口不证明全部外部依赖或
Native 登录行为兼容。相应功能需另做候选包绑定的运行时验收。

`build-report.json` 区分本次所选检查 `Passed` 与离线验收完整性 `LocalReady`。
默认只做静态检查，因此成功退出也不代表 `LocalReady` 或允许发布。
运行实际插件契约和专属回归，必须显式追加：

```powershell
  -TrustPluginCode -Profile C:/PluginSource/Tests/IndependentUpgrade.ps1
```

`Profile` 是调用者选择的受信任脚本，不从插件清单取执行命令。其契约、限时子进程、
合成数据和报告规则详见 [PLUGIN_DELIVERY.md](PLUGIN_DELIVERY.md)。
可用 `-CoreBaseline` 传入此前保存的核心源码/发布清单，防止只与本次临时基线比较。
六个核心源码目录与冻结发布目录会在构建及门禁前后核对；bin/obj 不计入源码清单。

## 兼容矩阵与差异

`tools/Test-PluginCompatibilityMatrix.ps1` 也能独立运行，接受旧/新实际压缩包与展开目录、
`-Hosts` 数组（1–8 个实际发布目录）和新的 `-EvidenceDirectory`。
跨进程调用可以改用 `-HostListFile` JSON 路径数组，不能同时提供两种列表。

每行都编译并运行 `plugin-sdk/ManifestCheck`，引用这一行对应的原始 Host/Abstractions DLL。
工具没有包依赖，使用空的本地还原源；不会静默改用当前开发 SDK，旧 SDK 缺少检查 API 时记录失败。

| 状态 | 含义 |
| --- | --- |
| `compatible` | 该实际宿主通过此包的静态元数据检查；不等于功能可用 |
| `incompatible` | 已完成检查，明确存在 SDK、特性、schema 或清单不兼容 |
| `check-failed` | 无法完成检查、宿主/探针改变或工具异常；不能用作兼容证据 |

`Complete` 表示检查完整，允许包含不兼容的负向结果；构建入口另外要求所选目标宿主的候选包兼容。
报告列出 SDK 下限、API 范围、schema、特性、提供方、能力、页面入口/文档版本和设置键的差异。
凭据别名（键、作用域、旧键）、旧设置别名和评论/页面图片域名的增删会标出 `AccessReviewRequired`。
设置控件的全部语义、平台 API 的隐式行为和任意代码权限不在该差异摘要内。
这些是访问**声明**，不是操作系统强制权限；没有差异也不能跳过新版本的人工信任批准。

`matrix.json` 绑定运行 ID、工具来源、旧/新包及展开内容的哈希、每个宿主的哈希和本地证据清单。
复查只读取指定证据目录，不执行任何代码，也不追随报告中的外部主机、源码或包路径：

```powershell
Import-Module ./tools/PluginRevision.psm1
Test-RevisionMatrixEvidence artifacts/example-revision-check/compatibility
Import-Module ./tools/PluginDelivery.psm1
Test-DeliveryEvidence artifacts/example-revision-check/delivery
```

哈希用于发现改动和混用证据，不提供发布者签名或防伪保证。
`ReleaseReady` 在本地构建/静态矩阵中始终为 false。

## 本阶段验证与边界

针对两个实际插件，独立构建各自 DLL、ZIP，并在冻结 SDK 2.10 上执行新旧版本对照。
插件专属离线配置使用原始合成数据、内存凭据库和阻断真实网络的替身；页面测试使用未改动的
发布 Web 资源与真实 Native 协调层投影，覆盖深浅色、1/1.5/2 浏览器缩放、窄窗、空库、键盘、
减少动画、错误恢复和撤销页面上下文。
浏览器缩放不是 Windows PerMonitorV2 或原生音视频验收。

Bilibili 配置对照旧版展示卡片与新版可播放媒体卡片，保留日期与讨论入口，确认浏览不自动播放，
文本动态不变成音频，取消/错误不变成空成功；QQ 配置复用独立发现与目录的回归。
这不是新增一轮平台业务功能，而是证明已有功能可通过独立插件包构建和验收。

运行工具守卫：

```powershell
pwsh -NoProfile -File tools/Test-PluginRevisionGuards.ps1
pwsh -NoProfile -File tools/Test-PluginDeliveryGuards.ps1
```

真实平台网络、登录、取流、声音及安装版 Native 验收没有在这里执行，既有线上失败不能由离线结果覆盖。
第十二阶段已接入[候选包更新计划与回退预检](PLUGIN_UPDATES.md)：用精确包哈希、兼容性和声明差异生成安装前计划，
区分仅插件可更新与需要基础 SDK 的情形，在隔离目录验证旧包重新导入及身份保留；不自动安装、不引入更新服务器。
下一步将差异预览与明确的恢复入口接入通用管理界面，继续保留新修订的独立信任、默认关闭和重启边界。
