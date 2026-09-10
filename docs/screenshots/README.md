# 文档截图 / Documentation screenshots

图片使用当前播放器的真实 HTML/CSS/JavaScript 资源，在隔离的 Microsoft Edge 中渲染。
它们是界面演示截图，不是 Windows 原生窗口截图，也不证明音频已经解码或发声。
图片未通过后期修改隐藏界面入口；截图中的控件文字以当前应用实际界面为准。

These images render actual application resources in isolated Microsoft Edge.
They are UI demonstrations, not native Windows captures or evidence of audio decoding/output.
No controls were removed or retouched; visible labels reflect the current application.

## 内容 / Contents

| 文件 / File | 场景 / Scene | 主题 / Theme |
| --- | --- | --- |
| library-zh-CN.png / library-en-US.png | 本地曲库 / Local library | 浅色 / Light |
| player-zh-CN.png / player-en-US.png | 唱片与歌词 / Record and lyrics | 深色 / Dark |
| customize-zh-CN.png / customize-en-US.png | 播放页设置预览 / Player customization | 浅色 / Light |

- 1440×960 pixels, 100% browser scale, reduced motion, paused demonstration state.
- 曲目、歌词、48 kHz / 24 bit / 941 kbps 等音质值为演示数据；不是实测值。
- Track names, lyrics, and quality values are demonstration data, not measurements.
- 封面使用项目自有的 public-preview.svg；不含私人曲库、账号或第三方唱片封面。
- Artwork uses the project's public-preview.svg. No personal library, accounts, or third-party album covers.
- 捕获日期 / Captured: 2026-09-10. Application resources: core commit 423f7bd.
- 本次电脑截图服务未能初始化，采用此可复现路径，不冒充原生验收。
- Native capture was unavailable for this session; this workflow is explicitly not native acceptance.

## 重新生成 / Reproduce

在仓库根目录执行 / From the repository root:

```powershell
npm ci --ignore-scripts
node tools/Capture-ReadmeScreenshots.cjs
```

使用发布资源时，可先将 AURALIS_UI_ROOT 环境变量设为发布目录中的 wwwroot 绝对路径。
To use published resources, set AURALIS_UI_ROOT to the absolute path of the published wwwroot first.

脚本只绑定 loopback、阻止外部页面请求、使用临时浏览器配置，不访问账户、个人媒体或原生播放器。
The script binds to loopback, blocks external page requests, and uses isolated browser contexts.
It does not access accounts, personal media, or the native playback backend.
