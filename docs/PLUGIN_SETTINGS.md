# 插件声明式设置

## 第五阶段：Settings v2（2026-09-13）

开发 Host SDK **2.8.0** 新增 `settings.v2`。Abstractions 1.9.0、API 1、schema 5 和程序集身份不变。
旧 `settings.v1` 平铺设置继续使用；使用任一新字段时，清单必须要求 schema 5、
minimumHostSdkVersion 至少 2.8.0，以及 settings.v1 / settings.v2。旧基础宿主应明确拒绝，而不是静默忽略条件。

新字段仍在每项 settings 内声明，无 HTML、CSS、脚本、表达式或秘密字段：

- `labelEn`、`descriptionEn`，以及 choice 的 `labelEn`：可选英文；缺失时回退原文。
- `group`：{id,label,labelEn?,description?,descriptionEn?}。同一来源内相同 ID 归并成一个 Fluent 分组；
  同组元数据必须完全相同，否则静态拒绝。顺序按清单第一次出现，组内按原顺序；未分组项兼容旧布局。
- `when`：{key,value,hint,hintEn?}。仅依赖**同一来源内一个无条件 choice** 的已保存精确值。
  自引用、未知 key/value、跨来源、endpoint 父项、链式依赖和循环均在发现阶段拒绝。
  hint 必填，解释为何暂不可编辑；不自动登录、不创建请求、不等同于账号授权判断。

每来源仍最多 16 项，因此分组也有界。key/group ID 最多 80 个限定 ASCII 字符；
标签最多 120 字符、说明/提示最多 400 字符，禁止控制字符。
清单快照的设置、选项和迁移别名集合不可变。校验不加载 DLL 或访问网络。

示例（省略组与英文说明）：

```json
[
  {"key":"ordering","kind":"choice","label":"排序","defaultValue":"default",
   "choices":[{"value":"default","label":"默认"},{"value":"custom","label":"自定义"}]},
  {"key":"direction","kind":"choice","label":"方向","defaultValue":"ascending",
   "choices":[{"value":"ascending","label":"升序"},{"value":"descending","label":"降序"}],
   "when":{"key":"ordering","value":"custom","hint":"选择自定义排序后可修改方向。"}}
]
```

### 条件、保存与数据所有权

- Native 为 UI 投影原偏好、声明元数据与 `enabled`。条件关闭时保留非敏感原偏好供用户理解，
  控件禁用且显示提示；插件的 `context.Settings.GetAsync` 返回 null，不读到失效配置。
- 条件重新满足后恢复原偏好；不删除、重置或迁移账号、本地曲库与收藏。
- `required` 仅在 enabled 时参与来源就绪和自动歌词匹配判断。
- 依赖读取和保存校验使用同一存储锁与已保存快照；Native / scoped store 均拒绝不适用项的迟到写入。
  不能靠修改 WebView 状态绕过。存储失败回滚内存，不把未提交父项当成已生效配置。
- UI 只在成功回执后启用依赖项；pending 时锁定编辑。声明/条件状态改变会废弃不兼容草稿与回执，
  仅标签/说明改变仍保留当前草稿。旧保存请求归属与超时规则不变。
- 平台字段及含义由插件拥有；核心只解释上述通用词汇。QQ 音质与网关地址已接入分组和英文说明，
  不为展示条件机制而给这些现有功能添加无意义的新开关。

### 证据与范围

- SettingsV2Tests：46 项 Native/Host 检查，涵盖惰性发现、兼容性、条件、实际合成能力路由、
  作用域、并发保存、重启、回滚及撤销；原设置测试保留。
- `npm run test:ui -- settings settingsv2`：真实应用资源 + 隔离 Edge + 合成 Native 消息；
  深浅色、100%/150%/200% deviceScaleFactor、640px 窄窗口、中英文、减少动态效果及无插件。
  这不等同于 Windows 原生 DPI、真实账号或声音验收。
- `tools/Test-PluginPageUpgrade.ps1` 固定同一套 15 个宿主运行文件，替换独立编译演示 DLL：
  新版增加两项设置，条件生效后**由插件读取方向并改变作品排序**，条件关闭恢复原顺序。
  报告：`artifacts/page-upgrade-3cee257b26c745568ce7e24de60d0474/result.json`。
- 纯核心发布输出：`artifacts/plugin-settings-core-stage5-20260913`；私人插件验证输出：
  `artifacts/plugin-settings-stage5-validated-20260913`。均为开发验证，不是已安装/已签名 MSIX 更新。
  已签名 0.16.11 / SDK 2.2 不支持本轮插件；不能只改版本号绕过检查。
- 本阶段不包含账号写操作、任意新控件、DLL 热替换或统一跨页面实体导航。契约内新增设置可只更新插件；
  超出当前词汇的原生能力仍需基础宿主升级。

## Settings v1 兼容规则

在线设置不按具体平台名称推断。Native 从已批准、启用且未停用的 Host 清单投影能力和设置。
账号需要 Authentication；登录按钮额外需要 NativeLogin；默认来源只含 TrackSearch。
歌词匹配需要 LyricsLookup；当前曲目的分 P/弹幕需要 MediaExtras，评论需要 Comments，封面切视频设置需要曲目具有视频。

## 清单与保存

设置声明在 schema 2 引入；当前运行包必须使用 schema 5，并声明 settings.v1 必需特性。旧格式只供离线识别，不在当前播放器启用。
每个 provider 最多 16 个设置，key 唯一，label/description 是限长纯文本，不接受插件 HTML 或脚本。

```json
"settings": [
  { "key": "quality", "label": "播放音质", "kind": "choice", "defaultValue": "auto",
    "choices": [{ "value": "auto", "label": "自动" }, { "value": "high", "label": "高品质" }] },
  { "key": "server", "label": "服务地址", "kind": "endpoint", "required": true }
]
```

- 只支持 choice / endpoint；枚举值必须属于清单，默认值同样校验。非法类型、重复键、null/超限字段使清单发现失败。
- endpoint 可为空；必填且为空时来源标记未配置，不调账号 API/自动搜索。非空仅接受 HTTPS 或 loopback HTTP，拒绝 userinfo/query/fragment。
- 只用于非敏感偏好；不提供密码、Cookie、Token 输入，秘密仍由隔离原生登录处理。
- Native 保存时重验路由、停用状态、声明 key 和允许值，不信任 Web 的校验结果。
- 保存为 platform-settings.json 的 plugin:<plugin-id>:<setting-key>；插件仍用 context.Settings.GetAsync(key) 读取。
- 迁移由插件可选的 `legacyKeys` 声明（每项最多 4 个不重复的普通键），核心不内置平台旧键。只在本插件新值不存在时读取声明的旧非敏感值，并按当前设置类型重新校验。显式空值和默认值都是已保存的覆盖值。
- 界面与 `context.Settings` 共用声明读取路径；未启用/未声明的字段返回 null，不能读其他插件作用域。旧值保留但不会默认分配给任意插件；非法旧地址/枚举不外传。
- 同插件内跨 provider 的设置 key 也不能重复，避免共用存储键却声明不同校验规则。网关插件自己处理旧共享地址与逐来源地址，不由核心猜测前缀。
- 保存失败回滚内存值，页面保留输入草稿和错误；匹配 requestId/providerId/key，15 秒未确认提示刷新。
- 配置快照移除 provider、移除设置键或改变类型/必填约束/允许选项时，Web 清除对应编辑草稿和待确认保存，
  取消其提示计时器，立即允许其它插件保存；旧回复不能给重新出现的同 ID 注入设置或成功/失败提示。
  普通刷新（名称、说明、实际值改变但声明约束相同）保留有效等待和草稿。这里只结束 Web 等待，不撤销已完成的 Native 保存，
  也不代替后台停用/声明校验；重新出现的设置以最新 Native 快照为准。
- 发现和设置编辑不加载 DLL、不访问平台网络；实际能力调用仍经 Host 惰性路由。

音质选项、优先级/降级与网关请求实现属于插件，主程序只处理声明类型和允许值。
旧非敏感键由插件通过 legacyKeys 显式声明，不补出旧包未声明的字段，也不推测地址前缀。
没有 NativeLogin 能力就不提供交互登录按钮；设置不能冒充秘密凭据输入。

## 验证边界

Host 测试覆盖设置格式惰性发现、非法字段/危险地址拒绝、声明式旧值读取、作用域/持久化、清空与默认值覆盖。
具体平台应在独立实现仓库验证网关清空后零 HTTP 请求、音质优先级/权限降级等业务，不将其专属测试代码编入核心。
Test-OnlineSettings.cjs 覆盖空插件、只搜索、仅账号、任意 ID 设置、键盘、失败/迟到确认及平台移除；
light/dark × 100/150/200%，200% 使用窄窗口。这不是 Windows 原生跨屏 DPI 验收。
未修改真实用户资料；新包导入后的真实账号/网络与 MSIX 升级仍待验收。
