# 候选插件更新计划与回退预检

第十二阶段，2026-09-13。新增开发工具，不修改本体、Host、Abstractions 或 Web 协议。
它将[独立交付检查](PLUGIN_DELIVERY.md)与[实际宿主兼容矩阵](PLUGIN_REVISIONS.md)绑定为可审阅的
更新计划，并可在新建隔离目录验证正式导入管理器的版本切换/重新导入旧包路径。

**没有自动安装、自动更新服务器、图形回退按钮、活动 DLL 热替换或真实账号迁移。**
不要把计划文件交给命令执行器；步骤文案只是说明，不是可执行脚本或安装授权。

## 输入与运行

从仓库根目录运行 PowerShell 7.2+。默认只生成计划，读取调用者明确选择的文件，不执行平台插件。
以下路径为示例，请替换为实际的新旧包、展开目录和上一阶段证据。

```powershell
./tools/New-PluginUpdatePlan.ps1 `
  -PreviousPackage artifacts/old/example.plugin-1.0.0.auralis-plugin `
  -PreviousDirectory artifacts/old/platforms/example.plugin `
  -UpdatedPackage artifacts/new/example.plugin-1.1.0.auralis-plugin `
  -UpdatedDirectory artifacts/new/platforms/example.plugin `
  -FrozenCore artifacts/frozen-player `
  -MatrixDirectory artifacts/example-revision-check/compatibility `
  -DeliveryDirectory artifacts/example-revision-check/delivery `
  -OutputDirectory artifacts/example-update-plan
```

`OutputDirectory` 必须不存在，父目录必须存在且不能是链接；输出不能放进包、宿主、源文件或输入证据目录。
失败目录保留，重跑使用新目录。可用 `-CoreBaseline` 指定此前的绝对路径 → SHA-256 核心基线，
否则仅证明本次运行前后相同，不证明这些文件与某次历史构建相同。

计划重新核对实际 ZIP 与展开文件，要求同 ID、递增版本和不同内容；从已验证清单重新计算声明差异。
兼容矩阵必须来自这两个精确包，且包含当前选定发布目录的精确清单/哈希。交付报告也必须绑定同一对包和宿主。
仅文件名或版本号相同不足以复用证据。报告中的外部源码路径不被打开，所选文件仍由命令行明确提供。

交付报告可以不提供，但结果只能是待验收，不能推断运行时通过。这里复核的是上阶段证据的关联与完整性，
**不会重新执行其业务/UI 检查，也不会刷新真实平台可用性或账号有效性**。

## 四种决策

| `Status` | 计划含义 | 本阶段动作 |
| --- | --- | --- |
| `plugin-only` | 同一宿主静态兼容新旧包，精确候选包已有完整本地交付证据 | 可做隔离预检；仍需独立信任批准、启用和重启 |
| `host-upgrade-required` | 候选明确要求所选宿主没有的 SDK/特性/API/schema | 保留旧版，不导入候选；找到基础宿主后重新做完整检查 |
| `verification-required` | 缺少交付证据、检查失败/不完整或宿主检查未完成 | 停在导入前，补齐当前包及当前宿主的检查 |
| `rejected` | 身份/文件/证据不匹配、提供方被删除、旧版不可作回退基线或其他清单错误 | 不替换旧版，修正后重新规划 |

只有可由基础宿主演进解释的明确不兼容才生成升级提示；非法清单不能伪装成“升级播放器即可”。
提供方 ID 删除/改名暂时拒绝，需要另做实体迁移设计；不能删除旧收藏或按标题猜测身份。

`AccessReviewRequired` 来自凭据别名、旧设置别名、图片域名声明的变化。它不是系统权限沙箱，也不覆盖
任意代码和全部设置语义；没有声明变化仍需审阅来源、批准每个新修订。计划始终保持
`RequiresTrust:true`、`RequiresRestart:true`、`AutomaticInstallAllowed:false`、`ReleaseReady:false`。

## 隔离回退预检

在上述命令后追加 `-Rehearse`。仅 `plugin-only` 可以执行。存在访问声明变化时，还需显式
`-AcknowledgeDeclarationChanges` 才进行隔离演练；此开关**不批准用户安装，不读写任何真实凭据**。
使用 .NET 8 编译无包依赖的 `plugin-sdk/UpdateRehearsal`，空本地还原源避免下载；引用精确发布 SDK，
不构建 Host 或其它插件。所选宿主 DLL 是受信任代码，不是恶意 SDK 的隔离沙箱。

预检使用真正的 `PlatformPluginManager`，在本次证据目录内执行：

1. 空目录无插件；预览不选中插件；未批准确认失败。
2. 导入旧包，验证默认关闭，明确启用后创建对应的下一次启动计划。
3. 取消候选、重放已取消预览、篡改预览、预先取消请求：原选择保持不变。
4. 导入候选，确认不会继承启用状态，不覆盖旧修订；明确启用后创建新版选择快照。
5. 使用一个明确标注的“模拟候选失败”决策点，走正式的旧包重新导入/批准/启用路径。
6. 验证恢复的旧包逐文件一致、新旧已提交修订均保留；停用时收藏仍可读取。

演练不是在运行中的播放器触发崩溃，也不代表自动恢复能力。它不加载平台 DLL，不调用登录、取流、
声音或清理真实账号；“下一次启动计划”只验证元数据快照，没有启动 WPF、实际重启或 DLL 卸载测试。

收藏检查链接未修改的 Native `SavedPlaylistStore`，使用显式合成路径，包含本地、在线和插件缺失引用。
每个阶段逐字节核对合成歌单、曲库哨兵和非敏感偏好，并重新读取稳定实体引用。
没有访问真实 `library.json`、用户收藏、Cookie、Windows 凭据库或 WebView 登录资料。

## 输出与复查

`update-plan.json` 包含运行 ID、精确包摘要、目标宿主、差异、关联证据的运行 ID/报告摘要、
派生决策、预检状态、冻结核心核对与本次证据文件清单。`update-plan.md` 是可读摘要；不兼容/待检查/拒绝
状态的步骤不会建议立即导入。`rehearsal/result.json` 记录各阶段版本、启用选择、合成状态摘要和断言数量。

```powershell
Import-Module ./tools/PluginUpdatePlan.psm1
Test-PluginUpdateEvidence artifacts/example-update-plan
pwsh -NoProfile -File tools/Test-PluginUpdatePlanGuards.ps1
```

复查只读取指定证据树，不追随其外部路径或执行命令。SHA-256 用于发现混包、旧证据及文件变化，
不是发布者签名；能够重写整个报告的人也能伪造这些摘要。不得把本地证据目录提交到公开源码。
脚本进程退出 0 表示所选计划/预检完成；2 表示需补充条件；1 表示拒绝或失败。调用层也应读取结构化状态，
不能将任意非零误写成“没有导入”，更不能将 0 当成发布批准。

## 本阶段验收范围

- 41 项工具守卫：决策分流、精确包/宿主关联、重新计算差异、输入篡改、混用报告、权限/重启标记和证据复查。
- 实际 Bilibili 1.17 → 1.18 与 QQ 1.15 → 1.16：各 55 项固定 SDK 管理器/合成存储检查通过，平台 DLL 加载数为 0。
- Bilibili SDK 2.9 生成基础宿主升级计划；缺交付证据暂停；套用 QQ 报告被拒绝，均不执行预检。
- 默认只生成计划路径单独通过：`plugin-only` / `not-run`，无探针输出目录，历史核心基线 649 文件不变。
- Release 构建、本地核心/Host、原平台 50 项、既有拆分插件夹具 54 项、核心发布边界及契约 89 项回归通过。
  拆分回归首次未指定 `AURALIS_SPLIT_PLUGIN_ROOT` 导致 fixture 缺失；指定既有隔离构建目录后重跑通过，未读取用户插件。
- 上阶段业务/UI 结果仅作精确关联的输入，不伪装成本阶段重新执行；真实平台及 Native 仍未验收。

QQ 1.16 此前真实搜索的 `ServiceUnavailable` 没有在这里修复或重测。本阶段不安装、不推送、不发布。

后续第十三阶段已把更新差异预览与“选择旧版包”入口接入通用插件管理页，详见
[PLUGIN_UPDATE_UI.md](PLUGIN_UPDATE_UI.md)。它使用正式管理器读取实际包，不执行本工具的计划文件，
也不把界面的静态兼容当成本工具的交付门禁。该阶段有一次管理基础能力的本体/Host/Web 更新；
平台 SDK 不变，平台自身在既有契约内新增页面和功能仍只更新插件。
