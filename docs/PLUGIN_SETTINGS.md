# 插件声明式设置

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
