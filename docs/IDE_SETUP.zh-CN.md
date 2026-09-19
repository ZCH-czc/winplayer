# 在 IDE 中打开 Auralis

[English](IDE_SETUP.md)

需要查看完整公开开发工程时，打开仓库根目录的 **`Auralis.Development.sln`**。
这是传统 Visual Studio 解决方案，可供 Windows 上的 Visual Studio、Rider 和兼容的 C# IDE 工具读取。
原来的 **`Auralis.sln`** 保持不变，继续作为较小的正式构建及 CI 入口。

## 准备和启动

1. 准备 Windows 10/11、.NET 8 SDK、Windows SDK 目标支持和 WebView2 Runtime。
2. Visual Studio 2022 安装“.NET 桌面开发”工作负载及 .NET 8 支持。
3. 打开解决方案、还原 NuGet 包，将 **Auralis** 设置为唯一启动项目，选择 Debug 或 Release 后构建。
4. 调试前先从托盘退出已有播放器，避免单实例机制把操作转发到旧实例。

```powershell
dotnet restore Auralis.Development.sln
dotnet build Auralis.Development.sln -c Release --no-restore
dotnet run --project Auralis/Auralis.csproj -c Debug
```

解决方案的 `Any CPU` 是托管项目的构建配置，不代表安装包支持任意架构；当前正式打包仍是 Windows x64。
能够读取解决方案也不代表 WPF 播放器能在其他操作系统运行。

## 工程组织

32 个项目按播放器、播放、媒体传输、图片、平台契约与宿主、测试、SDK 与示例、诊断工具分组。
文档也能从解决方案资源管理器直接打开。网页 UI 位于播放器项目的 `Auralis/wwwroot` 中；
运行桌面播放器不需要另开前端开发服务器，只有浏览器测试需要 Node 开发依赖。

构建解决方案不会运行测试、安装插件、批准信任，也不改变播放器的项目依赖关系。
诊断工具可能需要显式参数、准备好的文件或交互窗口，应依照各自文档单独运行，不能当作多个启动项目一起启动。
现有测试程序使用文档中的 `dotnet run`，并非全部通过 IDE 测试适配器运行。

`plugin-sdk/ManifestCheck` 与 `plugin-sdk/UpdateRehearsal` 需要
`-p:FrozenCore=<明确指定的已发布播放器目录>`，因此不纳入默认构建；不能用当前 SDK 偷换其冻结宿主测试。
尚未集成的实验运行时及已废弃安装器也不纳入正式构建。

## 基础验证

```powershell
dotnet build Auralis.sln -c Release --no-restore
dotnet run --project Auralis.Tests -c Release
dotnet run --project Auralis.Platform.Host.Tests -c Release
```

不要提交 `.vs`、`.idea`、`bin`、`obj`、个人启动配置、账号资料或凭据。
本次仅增加 IDE 工程组织，不增加播放器运行功能，也不改变 MSIX 版本。
