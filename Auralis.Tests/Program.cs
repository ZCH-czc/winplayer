using Auralis.Services;
using Auralis.Models;
using Auralis.Platform.Abstractions;
using Auralis;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static int ReserveLoopbackPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

if (args.Length > 0 && string.Equals(args[0], "--lan-preview", StringComparison.OrdinalIgnoreCase))
{
    await RunLanPreviewAsync();
    return;
}

CommentAvatarTests.Run();
AudioInformationTests.Run();
if (SingleInstanceTests.RunChild(args)) return;
SingleInstanceTests.Run();
await SavedPlaylistIdentityTests.RunAsync();
var savedTestDirectory = Path.Combine(Path.GetTempPath(), "auralis-saved-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(savedTestDirectory);
try
{
    var savedPath = Path.Combine(savedTestDirectory, "saved-playlists.json");
    var savedStore = new SavedPlaylistStore(savedPath);
    await savedStore.ChangeAsync("create", name: "多平台收藏");
    var savedId = (await savedStore.LoadAsync()).Single().Id;
    var onlineEntry = new SavedPlaylistEntry("online", null, "fixture.parts", "collection-A:22", "P2 测试", "作者", "专辑", 90, "collection-A:22");
    await savedStore.ChangeAsync("add", savedId, entry: onlineEntry);
    await savedStore.ChangeAsync("add", savedId, entry: onlineEntry with { Id = "duplicate" });
    await savedStore.ChangeAsync("add", savedId, entry: new("local", "local-id", null, null, "本地", "歌手", "专辑", 60, null));
    var loaded = (await new SavedPlaylistStore(savedPath).LoadAsync()).Single();
    Assert(loaded.Entries.Count == 2 && loaded.Entries[0].EntityId!.EndsWith(":22"), "混合歌单重启后保留引用与分 P，重复添加去重");
    var serialized = await File.ReadAllTextAsync(savedPath);
    Assert(!serialized.Contains("http") && !serialized.Contains("Cookie") && !serialized.Contains("handle"), "歌单不得保存播放授权或临时 handle");
    Assert(!SavedPlaylistStore.IsValid(onlineEntry with { EntityId = "https://evil.test/audio?token=secret" }), "拒绝把播放 URL 当作曲目标识保存");
    Assert(!SavedPlaylistStore.IsValid(onlineEntry with { DurationSeconds = double.NaN }), "拒绝无效时长");
    var fallbackTrack = SavedTrackMetadata.Fallback(loaded.Entries[0]);
    var fullTrack = fallbackTrack with { ArtworkUrl = new Uri("https://example.test/cover.png") };
    Assert(fallbackTrack.MusicVideo?.Id.Value == onlineEntry.VideoId, "重启后的引用保留原视频和分 P 入口");
    Assert(SavedTrackMetadata.Match(fallbackTrack.Id, new[] { fallbackTrack, fullTrack }) == fullTrack, "优先保留完整封面与平台信息，不被简化引用覆盖");
    Assert(SavedTrackMetadata.Match(fallbackTrack.Id, new[] { fullTrack with { Id = new("fixture.parts", "collection-A:11") }, fullTrack with { Id = new("fixture.other", fallbackTrack.Id.Value) } }) is null, "同名不同分 P 或不同平台不能补错封面与音轨");
    await savedStore.ChangeAsync("remove", savedId, entryId: "online");
    Assert((await savedStore.LoadAsync()).Single().Entries.Single().LocalId == "local-id", "移除引用不改变其他歌曲");
    await File.WriteAllTextAsync(savedPath, "broken original");
    try { await savedStore.ChangeAsync("create", name: "不得覆盖损坏文件"); throw new InvalidOperationException("Expected malformed file rejection"); }
    catch (System.Text.Json.JsonException) { }
    Assert(await File.ReadAllTextAsync(savedPath) == "broken original", "损坏文件保留，不静默覆盖收藏");
}
finally { Directory.Delete(savedTestDirectory, true); }

var clipboardAttempts = 0;
var clipboardDelay = 0;
var clipboardRecovered = await ClipboardWriteRetry.TryAsync(() =>
{
    if (++clipboardAttempts < 3) throw new System.Runtime.InteropServices.COMException("test busy");
    return Task.CompletedTask;
}, ms => { clipboardDelay += ms; return Task.CompletedTask; });
Assert(clipboardRecovered && clipboardAttempts == 3 && clipboardDelay == 180, "剪贴板短暂占用时异步重试后成功");
clipboardAttempts = 0;
clipboardDelay = 0;
var clipboardFailed = await ClipboardWriteRetry.TryAsync(() =>
{
    clipboardAttempts++;
    throw new System.Runtime.InteropServices.ExternalException("test busy");
}, ms => { clipboardDelay += ms; return Task.CompletedTask; });
Assert(!clipboardFailed && clipboardAttempts == 5 && clipboardDelay == 780, "剪贴板持续占用必须有界结束以展示手动复制入口");
var freshAudioOptions = AudioPlaybackStartOptions.Create(1000, 1, true, 0);
Assert(!freshAudioOptions.Any(option => option.StartsWith(":start-")), "点击新歌曲不能跳过开头或先播放再暂停");
var restoredAudioOptions = AudioPlaybackStartOptions.Create(1000, 1.25, false, 12.5);
Assert(restoredAudioOptions.Contains(":start-paused") && restoredAudioOptions.Contains(":start-time=12.5") &&
    restoredAudioOptions.Contains(":rate=1.25"), "恢复进度、暂停与倍速必须在解码前配置");
Assert(AudioPlaybackStartOptions.Create(0, double.NaN, true, double.NaN).Contains(":rate=1"), "无效起播参数安全退化");

var synced = LyricsParser.Parse("[ar:测试歌手]\n[offset:250]\n[00:01.50]第一行\n[00:03.25][00:05.00]重复行");
Assert(synced.Count == 3, "应解析单时间戳和多时间戳歌词");
Assert(Math.Abs((synced[0].TimeSeconds ?? 0) - 1.75) < .001, "应应用 offset 标签");
Assert(synced[1].Text == "重复行" && Math.Abs((synced[1].TimeSeconds ?? 0) - 3.5) < .001, "应解析小数时间");
Assert(synced[2].Text == "重复行" && Math.Abs((synced[2].TimeSeconds ?? 0) - 5.25) < .001, "应展开多时间戳");

var plain = LyricsParser.Parse("[ti:标题]\n第一句\n\n第二句");
Assert(plain.Count == 2, "应过滤空行和元数据标签");
Assert(plain.All(line => line.TimeSeconds is null), "普通歌词不应伪造时间轴");

var empty = LyricsParser.Parse("[ar:歌手]\n[offset:0]\n");
Assert(empty.Count == 0, "只有元数据时应返回空歌词");

var taskbarLyrics = new LyricsResponse(
    "taskbar-lyrics",
    "本地 LRC",
    true,
    false,
    [
        new LyricLine(1, "第一句"),
        new LyricLine(3, "第二句"),
        new LyricLine(3, "Second line"),
        new LyricLine(6, "第三句")
    ],
    string.Empty);
Assert(
    TaskbarLyricProjection.ResolvePrimaryText(taskbarLyrics, .5, "测试歌曲") == "测试歌曲",
    "任务栏歌词在第一句开始前应回退到歌名");
Assert(
    TaskbarLyricProjection.ResolvePrimaryText(taskbarLyrics, 3.2, "测试歌曲") == "第二句",
    "任务栏歌词应按时间选择当前行，并优先显示同时间戳的原文");
Assert(
    TaskbarLyricProjection.ResolvePrimaryText(taskbarLyrics, 8, "测试歌曲") == "第三句",
    "任务栏歌词在尾声应保留最后一行");
Assert(
    TaskbarLyricProjection.ResolvePrimaryText(null, 3, "测试歌曲") == "测试歌曲",
    "没有歌词时任务栏卡片应继续显示歌名");
Assert(
    TaskbarLyricProjection.ResolvePrimaryText(
        taskbarLyrics with { IsSynced = false, Lines = [new LyricLine(null, "静态歌词")] },
        12,
        "测试歌曲") == "静态歌词",
    "非逐行歌词应在任务栏显示第一条有效文本");
Assert(
    TaskbarLyricProjection.ResolvePrimaryText(taskbarLyrics with { Instrumental = true }, 12, "纯音乐") == "纯音乐",
    "纯音乐标记不应在任务栏制造伪歌词");

Assert(
    DesktopLyricsMaskStyle.ResolveAlpha(36) == 92,
    "桌面歌词默认遮罩亮度应保持现有 36% 视觉强度");
Assert(
    DesktopLyricsMaskStyle.ResolveAlpha(-100) == 26 && DesktopLyricsMaskStyle.ResolveAlpha(1000) == 230,
    "桌面歌词遮罩亮度必须限制在 10% 到 90% 的可用范围");
Assert(
    DesktopLyricsMaskStyle.ResolveAlpha(double.NaN) == 92,
    "无效的桌面歌词遮罩亮度应安全回退到默认值");

Assert(
    WindowsAudioEndpointLatencyProbe.ReferenceTimeToMilliseconds(100_000) == 10,
    "WASAPI 的 100 纳秒 REFERENCE_TIME 必须正确换算为毫秒");
Assert(
    WindowsAudioEndpointLatencyProbe.ReferenceTimeToMilliseconds(-1) == 0,
    "驱动返回的异常负延迟不得展示为负数");
Assert(
    WindowsAudioEndpointLatencyProbe.CalculateEstimatedLatency(true, 0, 10) is null,
    "蓝牙驱动未报告传输延迟时，不得把音频引擎周期伪装成蓝牙总延迟");
Assert(
    WindowsAudioEndpointLatencyProbe.CalculateEstimatedLatency(true, 24, 10) == 34,
    "蓝牙驱动明确报告流延迟时，才允许合成 Windows 共享路径估算");
Assert(
    WindowsAudioEndpointLatencyProbe.PropVariantSize == 8 + (2 * IntPtr.Size),
    "PROPVARIANT 必须匹配当前 Windows 进程位数，避免原生属性读取越界");
Assert(
    WindowsAudioEndpointLatencyProbe.IsBluetoothEnumeratorId("BTHENUM\\DEV_001122334455"),
    "Bluetooth Classic 设备枚举器应识别为蓝牙");
Assert(
    WindowsAudioEndpointLatencyProbe.IsBluetoothEnumeratorId("BTHLEDEVICE\\{test}") &&
    !WindowsAudioEndpointLatencyProbe.IsBluetoothEnumeratorId("USB\\VID_1234"),
    "蓝牙 LE 与有线设备枚举器必须正确区分");

var optInOnlyTaskbar = new TaskbarWidgetVisibilityPolicy();
Assert(!optInOnlyTaskbar.ShouldShow, "任务栏音乐组件在没有设置授权时必须默认隐藏");
optInOnlyTaskbar.UpdatePlayback(true);
Assert(!optInOnlyTaskbar.ShouldShow, "仅开始播放不得绕过任务栏组件主开关");
optInOnlyTaskbar.ApplyOptions(enabled: true, autoShowOnPlayback: true);
Assert(optInOnlyTaskbar.ShouldShow, "播放已经开始时，用户启用自动显示后应立即出现");
optInOnlyTaskbar.UpdatePlayback(false);
Assert(optInOnlyTaskbar.ShouldShow, "自动显示后的暂停状态应保留任务栏播放入口");
optInOnlyTaskbar.ApplyOptions(enabled: false, autoShowOnPlayback: true);
Assert(!optInOnlyTaskbar.ShouldShow, "关闭主开关必须立即隐藏并重置自动显示会话");
optInOnlyTaskbar.UpdatePlayback(false);
optInOnlyTaskbar.ApplyOptions(enabled: true, autoShowOnPlayback: true);
Assert(!optInOnlyTaskbar.ShouldShow, "重新启用自动显示后应等待下一次实际播放");
optInOnlyTaskbar.UpdatePlayback(true);
Assert(optInOnlyTaskbar.ShouldShow, "自动显示模式应在首次播放事件后出现");

var persistentTaskbar = new TaskbarWidgetVisibilityPolicy();
persistentTaskbar.ApplyOptions(enabled: true, autoShowOnPlayback: false);
Assert(persistentTaskbar.ShouldShow, "关闭自动显示时，用户显式启用的组件应立即常驻任务栏");
persistentTaskbar.ApplyOptions(enabled: true, autoShowOnPlayback: true);
Assert(!persistentTaskbar.ShouldShow, "空闲时切换为自动显示应收起组件并等待播放");

var taskbarUpdateGate = new TaskbarWidgetUpdateGate();
var visibilityTransitions = 0;
for (var update = 0; update < 4000; update++)
{
    if (taskbarUpdateGate.TryChangeEnabled(true))
    {
        visibilityTransitions++;
    }
}
Assert(
    visibilityTransitions == 1,
    "高频播放心跳不得重复执行任务栏卡片启用和定位入口");
var stableBounds = new TaskbarWidgetBounds(1200, 1036, 1494, 1076);
Assert(
    taskbarUpdateGate.ShouldApplyBounds(stableBounds, isNativeVisible: true),
    "首次任务栏几何位置必须应用");
taskbarUpdateGate.MarkBoundsApplied(stableBounds);
Assert(
    !taskbarUpdateGate.ShouldApplyBounds(stableBounds, isNativeVisible: true),
    "相同任务栏几何位置不得重复调用原生窗口定位");
Assert(
    !taskbarUpdateGate.ShouldApplyBounds(new TaskbarWidgetBounds(1201, 1035, 1495, 1077), isNativeVisible: true),
    "任务栏几何的 1–2 像素舍入抖动不得重复争抢顶层窗口位置");
Assert(
    taskbarUpdateGate.ShouldApplyBounds(new TaskbarWidgetBounds(1203, 1036, 1497, 1076), isNativeVisible: true),
    "跳过轻微抖动后必须仍以最后实际应用位置为基线，累积超过阈值时重新定位");
taskbarUpdateGate.MarkBoundsApplied(new TaskbarWidgetBounds(1203, 1036, 1497, 1076));
Assert(
    taskbarUpdateGate.ShouldApplyBounds(stableBounds, isNativeVisible: false),
    "原生窗口被系统隐藏后应允许安全恢复显示");
taskbarUpdateGate.ResetBounds();
Assert(
    taskbarUpdateGate.ShouldApplyBounds(stableBounds, isNativeVisible: true),
    "DPI 或显示器变化后必须重新应用任务栏几何位置");
Assert(
    TaskbarWidgetRecoveryBackoff.GetDelay(0) == TimeSpan.FromSeconds(5) &&
    TaskbarWidgetRecoveryBackoff.GetDelay(1) == TimeSpan.FromSeconds(10) &&
    TaskbarWidgetRecoveryBackoff.GetDelay(2) == TimeSpan.FromSeconds(20) &&
    TaskbarWidgetRecoveryBackoff.GetDelay(3) == TimeSpan.FromSeconds(40) &&
    TaskbarWidgetRecoveryBackoff.GetDelay(4) == TimeSpan.FromSeconds(60) &&
    TaskbarWidgetRecoveryBackoff.GetDelay(20) == TimeSpan.FromSeconds(60),
    "任务栏窗口恢复必须按 5/10/20/40/60 秒有界退避，持续失败不得紧循环");

var taskbarLeaseName = @"Local\Auralis.TaskbarWidget.Tests." + Guid.NewGuid().ToString("N");
using (var ownerReady = new ManualResetEventSlim())
using (var releaseOwner = new ManualResetEventSlim())
{
    var ownerAcquired = false;
    var ownerThread = new Thread(() =>
    {
        using var ownerLease = new TaskbarWidgetLease(taskbarLeaseName);
        ownerAcquired = ownerLease.TryAcquire();
        ownerReady.Set();
        releaseOwner.Wait();
    });
    ownerThread.Start();
    ownerReady.Wait();
    Assert(ownerAcquired, "第一个 Auralis 实例应获得任务栏卡片独占租约");

    var contenderOwned = false;
    var contenderThread = new Thread(() =>
    {
        using var contender = new TaskbarWidgetLease(taskbarLeaseName);
        contenderOwned = contender.TryAcquire();
    });
    contenderThread.Start();
    contenderThread.Join();
    Assert(!contenderOwned, "第二个进程线程不得同时获得同一任务栏卡片租约");

    releaseOwner.Set();
    ownerThread.Join();
    var takeoverOwned = false;
    var takeoverThread = new Thread(() =>
    {
        using var takeover = new TaskbarWidgetLease(taskbarLeaseName);
        takeoverOwned = takeover.TryAcquire();
    });
    takeoverThread.Start();
    takeoverThread.Join();
    Assert(takeoverOwned, "原实例释放后，等待中的实例应可安全接管任务栏卡片");
}

var incompatibleLeaseName = @"Local\Auralis.TaskbarWidget.Tests.Incompatible." + Guid.NewGuid().ToString("N");
using (var incompatibleObject = new EventWaitHandle(false, EventResetMode.ManualReset, incompatibleLeaseName))
using (var unavailableLease = new TaskbarWidgetLease(incompatibleLeaseName))
{
    Assert(!unavailableLease.TryAcquire(),
        "命名对象类型或访问控制冲突时，任务栏组件必须安全关闭而不是阻止应用启动");
}

Assert(LanAddressPolicy.IsAllowedPeer(IPAddress.Loopback), "局域网服务必须允许本机配对预览");
Assert(LanAddressPolicy.IsAllowedPeer(IPAddress.Parse("192.168.10.20")), "RFC1918 私有地址应允许访问");
Assert(LanAddressPolicy.IsAllowedPeer(IPAddress.Parse("172.20.1.4")), "172.16/12 私有地址应允许访问");
Assert(!LanAddressPolicy.IsAllowedPeer(IPAddress.Parse("8.8.8.8")), "公网地址不得访问局域网播放器");
Assert(!LanAddressPolicy.IsAllowedPeer(IPAddress.Any) &&
       !LanAddressPolicy.IsAllowedPeer(IPAddress.IPv6Any),
    "未指定监听地址不得被误判为可信局域网对端");

var lanSessionManager = new LanAccessSessionManager();
var firstPairingToken = lanSessionManager.AccessToken;
Assert(lanSessionManager.TryExchange(firstPairingToken, IPAddress.Loopback, out var boundSession) == LanPairingResult.Success &&
       !string.IsNullOrWhiteSpace(boundSession),
    "有效配对码应创建浏览器会话");
Assert(lanSessionManager.IsAuthorized(boundSession, IPAddress.Loopback),
    "浏览器会话应允许原配对设备继续访问");
Assert(!lanSessionManager.IsAuthorized(boundSession, IPAddress.Parse("127.0.0.2")),
    "浏览器会话 Cookie 不得被另一个网络地址横向复用");
Assert(lanSessionManager.TryExchange(firstPairingToken, IPAddress.Loopback, out _) == LanPairingResult.Invalid,
    "一次性配对码成功后必须立即失效");
lanSessionManager.RevokeAll();
Assert(!lanSessionManager.IsAuthorized(boundSession, IPAddress.Loopback),
    "撤销全部会话后旧 Cookie 必须立即失效");

var rateLimitedSessions = new LanAccessSessionManager();
for (var attempt = 0; attempt < 6; attempt++)
{
    Assert(rateLimitedSessions.TryExchange("invalid", IPAddress.Parse("192.168.1.25"), out _) == LanPairingResult.Invalid,
        "速率限制生效前的错误配对应返回 invalid");
}
Assert(rateLimitedSessions.TryExchange("invalid", IPAddress.Parse("192.168.1.25"), out _) == LanPairingResult.RateLimited,
    "同一对端持续猜测配对码时必须触发速率限制");

var lanSettingsTestDirectory = Path.Combine(Path.GetTempPath(), "AuralisLanSettingsTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(lanSettingsTestDirectory);
try
{
    var settingsPath = Path.Combine(lanSettingsTestDirectory, "lan-sharing.json");
    var lanSettingsStore = new LanSharingSettingsStore(settingsPath);
    var defaults = await lanSettingsStore.LoadAsync();
    Assert(!defaults.Enabled && defaults.Port == LanSharingSettingsStore.DefaultPort,
        "局域网播放器在无设置文件时必须默认关闭");

    await lanSettingsStore.SaveAsync(new LanSharingSettings(1, true, 80));
    var restored = await lanSettingsStore.LoadAsync();
    Assert(restored.Enabled && restored.Port == LanSharingSettingsStore.DefaultPort,
        "无效端口必须规范化，且设置只能保存显式启用状态");
    var settingsText = await File.ReadAllTextAsync(settingsPath);
    Assert(!settingsText.Contains("token", StringComparison.OrdinalIgnoreCase) &&
           !settingsText.Contains("secret", StringComparison.OrdinalIgnoreCase),
        "局域网设置文件不得持久化配对或会话材料");
    Assert(!File.Exists(settingsPath + ".tmp"), "成功保存设置后不得遗留临时文件");

    await File.WriteAllTextAsync(settingsPath, "{broken-json");
    var damaged = await lanSettingsStore.LoadAsync();
    Assert(!damaged.Enabled, "损坏的局域网设置必须安全回退为关闭");
}
finally
{
    Directory.Delete(lanSettingsTestDirectory, recursive: true);
}

var lanServerTestDirectory = Path.Combine(Path.GetTempPath(), "AuralisLanServerTests", Guid.NewGuid().ToString("N"));
var lanWebRoot = Path.Combine(lanServerTestDirectory, "web");
Directory.CreateDirectory(lanWebRoot);
try
{
    await File.WriteAllTextAsync(Path.Combine(lanWebRoot, "index.html"), "<!doctype html><title>Auralis LAN</title>");
    await File.WriteAllTextAsync(Path.Combine(lanWebRoot, "lan-player.js"), "'use strict';");
    await File.WriteAllTextAsync(Path.Combine(lanWebRoot, "lan-player.css"), ":root{color-scheme:light dark}");
    var audioPath = Path.Combine(lanServerTestDirectory, "private-name.mp3");
    var audioBytes = Enumerable.Range(0, 1024).Select(index => (byte)(index % 251)).ToArray();
    const long audioFileLength = 64L * 1024 * 1024;
    await using (var audioFile = new FileStream(audioPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
    {
        await audioFile.WriteAsync(audioBytes);
        audioFile.SetLength(audioFileLength);
    }
    var port = ReserveLoopbackPort();

    await using var lanServer = new LanMusicSharingService(
        NullAppLogger.Instance,
        lanWebRoot,
        addressProvider: () => [IPAddress.Parse("8.8.8.8")]);
    lanServer.ReplaceLibrary([
        new LanSharedTrack(
            "desktop-track-id",
            audioPath,
            "测试曲目",
            "测试艺术家",
            "测试专辑",
            "MP3",
            audioBytes.Length,
            12.5,
            DateTime.UtcNow,
            null)
    ]);
    await lanServer.ApplySettingsAsync(new LanSharingSettings(1, true, port));
    var lanState = lanServer.CurrentState;
    Assert(lanState.IsRunning && lanState.BaseUrls.Count == 1 && lanState.TrackCount == 1,
        "显式启用后必须过滤注入的公网地址，只在 loopback 启动并发布曲库快照");

    var baseUri = new Uri(lanState.BaseUrls.Single());
    using var unauthorizedClient = new HttpClient { BaseAddress = baseUri };
    var landing = await unauthorizedClient.GetAsync("");
    Assert(landing.IsSuccessStatusCode &&
           landing.Headers.Contains("Content-Security-Policy") &&
           !landing.Headers.Contains("Server"),
        "公开落地页必须带安全响应头且不得暴露 Kestrel Server 标识");
    var unauthorized = await unauthorizedClient.GetAsync("api/lan/v1/library");
    Assert(unauthorized.StatusCode == HttpStatusCode.Unauthorized,
        "未配对浏览器不得枚举本地曲库");

    var pairingUri = new Uri(lanState.PairingUrls.Single());
    var accessToken = Uri.UnescapeDataString(pairingUri.Fragment["#access=".Length..]);

    using (var wrongHostRequest = new HttpRequestMessage(HttpMethod.Get, ""))
    {
        wrongHostRequest.Headers.Host = $"example.invalid:{port}";
        var wrongHost = await unauthorizedClient.SendAsync(wrongHostRequest);
        Assert(wrongHost.StatusCode == HttpStatusCode.Forbidden,
            "非监听 IP/localhost 的 Host 头必须被拒绝，防止 DNS rebinding");
    }

    using (var wrongPortRequest = new HttpRequestMessage(HttpMethod.Get, ""))
    {
        wrongPortRequest.Headers.Host = $"127.0.0.1:{port + 1}";
        var wrongPort = await unauthorizedClient.SendAsync(wrongPortRequest);
        Assert(wrongPort.StatusCode == HttpStatusCode.Forbidden,
            "Host 头端口与监听端口不一致时必须拒绝");
    }

    using (var rejectedOriginRequest = new HttpRequestMessage(HttpMethod.Post, "api/lan/v1/session"))
    {
        rejectedOriginRequest.Headers.Add("Origin", "http://evil.invalid");
        rejectedOriginRequest.Content = JsonContent.Create(new { accessToken });
        var rejectedOrigin = await unauthorizedClient.SendAsync(rejectedOriginRequest);
        Assert(rejectedOrigin.StatusCode == HttpStatusCode.Forbidden,
            "带有跨源 Origin 的配对请求必须被拒绝");
    }

    using (var wrongContentRequest = new HttpRequestMessage(HttpMethod.Post, "api/lan/v1/session"))
    {
        wrongContentRequest.Content = new StringContent(
            $"{{\"accessToken\":\"{accessToken}\"}}",
            Encoding.UTF8,
            "text/plain");
        var wrongContent = await unauthorizedClient.SendAsync(wrongContentRequest);
        Assert(wrongContent.StatusCode == HttpStatusCode.UnsupportedMediaType,
            "配对接口只接受 JSON，避免被普通跨站表单调用");
    }

    var cookieContainer = new CookieContainer();
    using var pairedClient = new HttpClient(new HttpClientHandler
    {
        CookieContainer = cookieContainer,
        UseCookies = true
    }) { BaseAddress = baseUri };
    var paired = await pairedClient.PostAsJsonAsync("api/lan/v1/session", new { accessToken });
    Assert(paired.IsSuccessStatusCode, "有效的一次性 fragment 配对码应换取 HttpOnly 会话");
    var setCookie = string.Join(";", paired.Headers.GetValues("Set-Cookie"));
    Assert(setCookie.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase) &&
           setCookie.Contains("SameSite=Strict", StringComparison.OrdinalIgnoreCase) &&
           !setCookie.Contains(accessToken, StringComparison.Ordinal),
        "配对响应必须使用 HttpOnly/SameSite=Strict Cookie，且不能回显配对码");

    using var replayClient = new HttpClient { BaseAddress = baseUri };
    var replay = await replayClient.PostAsJsonAsync("api/lan/v1/session", new { accessToken });
    Assert(replay.StatusCode == HttpStatusCode.Unauthorized,
        "一次性配对码成功兑换后不得重放");

    var libraryResponse = await pairedClient.GetAsync("api/lan/v1/library");
    var libraryJson = await libraryResponse.Content.ReadAsStringAsync();
    Assert(libraryResponse.IsSuccessStatusCode && !libraryJson.Contains(audioPath, StringComparison.OrdinalIgnoreCase) &&
           !libraryJson.Contains("private-name.mp3", StringComparison.OrdinalIgnoreCase),
        "LAN 曲库响应不得泄露绝对路径或本机文件名");
    using var libraryDocument = System.Text.Json.JsonDocument.Parse(libraryJson);
    var trackElement = libraryDocument.RootElement.GetProperty("tracks")[0];
    var publicId = trackElement.GetProperty("id").GetString();
    var audioUrl = trackElement.GetProperty("audioUrl").GetString();
    Assert(trackElement.GetProperty("contentType").GetString() == "audio/mpeg",
        "LAN 曲库必须向浏览器声明真实音频 MIME，以便在播放前判断解码支持");
    Assert(!string.IsNullOrWhiteSpace(publicId) && !string.Equals(publicId, "desktop-track-id", StringComparison.Ordinal),
        "LAN 曲目 ID 必须是每次服务生命周期生成的不可猜测映射");

    using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, audioUrl);
    rangeRequest.Headers.Range = new RangeHeaderValue(10, 109);
    var rangeResponse = await pairedClient.SendAsync(rangeRequest);
    var rangeBytes = await rangeResponse.Content.ReadAsByteArrayAsync();
    Assert(rangeResponse.StatusCode == HttpStatusCode.PartialContent && rangeBytes.SequenceEqual(audioBytes[10..110]),
        "局域网音频必须使用框架 Range 处理并返回精确的 206 字节区间");
    Assert(rangeResponse.Headers.AcceptRanges.Contains("bytes") &&
           rangeResponse.Content.Headers.ContentDisposition?.DispositionType == "inline",
        "局域网音频必须显式允许 Range 并以内联媒体返回，兼容移动浏览器的媒体探测");

    using var headRequest = new HttpRequestMessage(HttpMethod.Head, audioUrl);
    var headResponse = await pairedClient.SendAsync(headRequest);
    Assert(headResponse.IsSuccessStatusCode && headResponse.Content.Headers.ContentLength == audioFileLength,
        "浏览器媒体探测需要 HEAD 返回正确长度且不下载正文");

    using var invalidRangeRequest = new HttpRequestMessage(HttpMethod.Get, audioUrl);
    invalidRangeRequest.Headers.Range = new RangeHeaderValue(audioFileLength + 10, audioFileLength + 20);
    var invalidRangeResponse = await pairedClient.SendAsync(invalidRangeRequest);
    Assert(invalidRangeResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable,
        "越界 Range 请求必须返回 416，不能退化为整文件响应");

    var unknown = await pairedClient.GetAsync("api/lan/v1/tracks/..%2Funknown/audio");
    Assert(unknown.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
        "未知或路径穿越形式的曲目 ID 不得映射到磁盘文件");

    var heldResponses = new List<HttpResponseMessage>();
    try
    {
        for (var index = 0; index < 6; index++)
        {
            using var heldRequest = new HttpRequestMessage(HttpMethod.Get, audioUrl);
            heldRequest.Headers.Range = new RangeHeaderValue(0, 32L * 1024 * 1024 - 1);
            heldResponses.Add(await pairedClient.SendAsync(
                heldRequest,
                HttpCompletionOption.ResponseHeadersRead));
        }

        using var capacityRequest = new HttpRequestMessage(HttpMethod.Get, audioUrl);
        capacityRequest.Headers.Range = new RangeHeaderValue(0, 0);
        var capacityResponse = await pairedClient.SendAsync(capacityRequest);
        Assert(capacityResponse.StatusCode == HttpStatusCode.TooManyRequests,
            "六个仍在传输的浏览器流必须触发有界并发保护");
    }
    finally
    {
        foreach (var response in heldResponses)
        {
            response.Dispose();
        }
    }

    HttpStatusCode recoveredStatus = HttpStatusCode.TooManyRequests;
    for (var attempt = 0; attempt < 20 && recoveredStatus == HttpStatusCode.TooManyRequests; attempt++)
    {
        await Task.Delay(50);
        using var recoveryRequest = new HttpRequestMessage(HttpMethod.Get, audioUrl);
        recoveryRequest.Headers.Range = new RangeHeaderValue(0, 0);
        using var recoveryResponse = await pairedClient.SendAsync(recoveryRequest);
        recoveredStatus = recoveryResponse.StatusCode;
    }
    Assert(recoveredStatus == HttpStatusCode.PartialContent,
        "浏览器主动中断多个 Range 后，所有流许可必须及时归还并允许下一首播放");

    lanServer.RevokeAllSessions();
    var revoked = await pairedClient.GetAsync("api/lan/v1/library");
    Assert(revoked.StatusCode == HttpStatusCode.Unauthorized,
        "用户撤销局域网会话后，已配对浏览器必须立即失去曲库访问权");

    await lanServer.ApplySettingsAsync(new LanSharingSettings(1, false, port));
    Assert(!lanServer.CurrentState.IsRunning && lanServer.CurrentState.ActiveSessionCount == 0,
        "关闭局域网播放器必须立即停服并撤销全部会话");

    var rebound = new TcpListener(IPAddress.Loopback, port);
    rebound.Server.ExclusiveAddressUse = true;
    rebound.Start();
    rebound.Stop();

    var occupiedPortListener = new TcpListener(IPAddress.Loopback, 0);
    occupiedPortListener.Server.ExclusiveAddressUse = true;
    occupiedPortListener.Start();
    var occupiedPort = ((IPEndPoint)occupiedPortListener.LocalEndpoint).Port;
    await lanServer.ApplySettingsAsync(new LanSharingSettings(1, true, occupiedPort));
    Assert(lanServer.CurrentState.Enabled &&
           !lanServer.CurrentState.IsRunning &&
           lanServer.CurrentState.ErrorCode == "bindFailed",
        "端口被占用时必须返回可恢复错误，不能崩溃或退回随机端口");
    occupiedPortListener.Stop();

    await lanServer.ApplySettingsAsync(new LanSharingSettings(1, true, occupiedPort));
    Assert(lanServer.CurrentState.IsRunning && lanServer.CurrentState.ErrorCode is null,
        "端口释放后再次应用同一设置应能恢复监听并清除旧错误");
    await lanServer.ApplySettingsAsync(new LanSharingSettings(1, false, occupiedPort));
    await lanServer.ApplySettingsAsync(new LanSharingSettings(1, true, port));
    Assert(lanServer.CurrentState.IsRunning, "退出回归使用真实运行的 Kestrel 服务");
    ShutdownContextTests.DisposeWithoutDispatcher(lanServer);
}
finally
{
    Directory.Delete(lanServerTestDirectory, recursive: true);
}

var folderStoreTestDirectory = Path.Combine(Path.GetTempPath(), "AuralisMusicFolderTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folderStoreTestDirectory);
try
{
    var parentFolder = Path.Combine(folderStoreTestDirectory, "音乐");
    var childFolder = Path.Combine(parentFolder, "现场");
    var otherFolder = Path.Combine(folderStoreTestDirectory, "播客");
    Directory.CreateDirectory(childFolder);
    Directory.CreateDirectory(otherFolder);

    var normalizedFolders = MusicFolderStore.Normalize([
        parentFolder + Path.DirectorySeparatorChar,
        parentFolder.ToUpperInvariant(),
        childFolder,
        otherFolder,
        "   "
    ]);
    Assert(normalizedFolders.Count == 3, "多文件夹设置应忽略空路径和大小写不同的重复路径");

    var watchRoots = MusicFolderStore.CollapseWatchRoots(normalizedFolders);
    Assert(
        watchRoots.Count == 2 && watchRoots.Contains(parentFolder, StringComparer.OrdinalIgnoreCase) &&
        watchRoots.Contains(otherFolder, StringComparer.OrdinalIgnoreCase),
        "父目录已监听时不应为其子目录再创建重复的 FileSystemWatcher");
    Assert(
        MusicFolderStore.IsPathWithinRoot(Path.Combine(childFolder, "歌曲.flac"), parentFolder),
        "父文件夹必须覆盖子目录中的歌曲");
    Assert(
        !MusicFolderStore.IsPathWithinRoot(parentFolder + "-备份\\歌曲.flac", parentFolder),
        "相似路径前缀不能被误判为文件夹子项");

    var settingsPath = Path.Combine(folderStoreTestDirectory, "settings", "music-folders.json");
    var folderStore = new MusicFolderStore(settingsPath);
    await folderStore.SaveAsync([parentFolder, childFolder, otherFolder]);
    var restoredFolders = await folderStore.LoadAsync();
    Assert(restoredFolders.Count == 3, "多个音乐文件夹应原样持久化并在下次启动时恢复");
}
finally
{
    Directory.Delete(folderStoreTestDirectory, recursive: true);
}

var testDirectory = Path.Combine(Path.GetTempPath(), "AuralisLyricsTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
var mediaFixtureFailed = false;
try
{
    var audioPath = Path.Combine(testDirectory, "本地测试.flac");
    var lrcPath = Path.ChangeExtension(audioPath, ".lrc");
    await File.WriteAllTextAsync(lrcPath, "[00:00.50]本地第一行\n[00:02.00]本地第二行");
    var stored = new StoredTrack(audioPath, DateTime.Now);
    var track = new TrackInfo("local-test", "本地测试", "测试歌手", "测试专辑", "本地测试.flac", "FLAC", null, 0, 10, DateTime.Now);

    using var service = new LyricsService(Path.Combine(testDirectory, "local-cache"));
    var localResult = await service.GetLyricsAsync(stored, track, false, CancellationToken.None);
    Assert(localResult.Source == "本地 LRC", "本地 LRC 必须优先于在线服务");
    Assert(localResult.IsSynced && localResult.Lines.Count == 2, "本地 LRC 应保留同步时间轴");
    Assert(
        localResult.SelectedSource == "local" &&
        localResult.Availability is { HasLocal: true, LocalSource: "本地 LRC", OnlineChecked: false },
        "歌词响应应明确当前选择和本地/在线可用状态");

    var compatibleDirectory = Path.Combine(testDirectory, "同名兼容匹配");
    Directory.CreateDirectory(compatibleDirectory);
    var compatibleCases = new[]
    {
        (Audio: "1 清晨测试.flac", Lyric: "1.清晨测试.lrc", Title: "清晨测试"),
        (Audio: "5 我的测试未完成.flac", Lyric: "5. 我的测试未完成.LrC", Title: "我的测试未完成"),
        (Audio: "8 夜风.flac", Lyric: "8.夜风 ver.2022.lrc", Title: "夜风")
    };
    foreach (var item in compatibleCases)
    {
        var compatibleAudioPath = Path.Combine(compatibleDirectory, item.Audio);
        var compatibleLyricPath = Path.Combine(compatibleDirectory, item.Lyric);
        await File.WriteAllBytesAsync(compatibleAudioPath, []);
        await File.WriteAllTextAsync(compatibleLyricPath, $"[00:01.00]{item.Title}歌词");
        var match = LocalLyricsLocator.Find(compatibleAudioPath, item.Title, "测试歌手Example");
        Assert(
            match is not null && Path.GetFullPath(match.Path) == Path.GetFullPath(compatibleLyricPath),
            $"应兼容序号分隔符、大小写和版本后缀：{item.Audio} -> {item.Lyric}");

        var compatibleTrack = new TrackInfo(
            "compatible-" + item.Title,
            item.Title,
            "测试歌手Example",
            "测试专辑",
            item.Audio,
            "FLAC",
            null,
            0,
            10,
            DateTime.Now);
        var compatibleResult = await service.GetLyricsAsync(
            new StoredTrack(compatibleAudioPath, DateTime.Now),
            compatibleTrack,
            false,
            CancellationToken.None);
        Assert(
            compatibleResult.Source == "本地 LRC" && compatibleResult.Lines.Single().Text == item.Title + "歌词",
            "兼容匹配必须进入现有本地歌词解析链路");
    }

    var unicodeDirectory = Path.Combine(testDirectory, "Unicode规范化");
    Directory.CreateDirectory(unicodeDirectory);
    var decomposedAudioPath = Path.Combine(unicodeDirectory, "Cafe\u0301.flac");
    var composedLyricPath = Path.Combine(unicodeDirectory, "Café.LRC");
    await File.WriteAllBytesAsync(decomposedAudioPath, []);
    await File.WriteAllTextAsync(composedLyricPath, "[00:01.00]Unicode 歌词");
    Assert(
        LocalLyricsLocator.Find(decomposedAudioPath)?.Path == composedLyricPath,
        "Unicode 组合字符与 LRC 扩展名大小写不应影响同名识别");

    var ambiguousDirectory = Path.Combine(testDirectory, "歧义歌词");
    Directory.CreateDirectory(ambiguousDirectory);
    var ambiguousAudioPath = Path.Combine(ambiguousDirectory, "9 新歌.flac");
    await File.WriteAllBytesAsync(ambiguousAudioPath, []);
    await File.WriteAllTextAsync(Path.Combine(ambiguousDirectory, "9.新歌 ver.2022.lrc"), "[00:01]版本一");
    await File.WriteAllTextAsync(Path.Combine(ambiguousDirectory, "9.新歌 ver.2023.lrc"), "[00:01]版本二");
    Assert(
        LocalLyricsLocator.Find(ambiguousAudioPath, "新歌", "测试歌手") is null,
        "多个同置信度兼容候选必须视为歧义，不能静默误配");

    var switchDirectory = Path.Combine(testDirectory, "来源切换");
    Directory.CreateDirectory(switchDirectory);
    var switchAudioPath = Path.Combine(switchDirectory, "来源切换.flac");
    await File.WriteAllBytesAsync(switchAudioPath, []);
    await File.WriteAllTextAsync(Path.ChangeExtension(switchAudioPath, ".lrc"), "[00:01]本地版本");
    var switchTrack = new TrackInfo(
        "source-switch",
        "来源切换",
        "测试歌手",
        "测试专辑",
        "来源切换.flac",
        "FLAC",
        null,
        0,
        10,
        DateTime.Now);
    var switchStored = new StoredTrack(switchAudioPath, DateTime.Now);
    var onlineLookupCalls = 0;
    using var switchService = new LyricsService(Path.Combine(testDirectory, "switch-cache"), (metadata, token) =>
    {
        token.ThrowIfCancellationRequested();
        onlineLookupCalls++;
        Assert(metadata.Title == switchTrack.Title, "插件只接收曲目匹配元数据");
        return Task.FromResult<PlatformLyricsLookupResult?>(new("示例歌词来源", "[00:01.00]在线版本"));
    });
    var automaticSource = await switchService.GetLyricsAsync(
        switchStored,
        switchTrack,
        true,
        "localFirst",
        "auto",
        CancellationToken.None);
    Assert(
        automaticSource.SelectedSource == "local" && automaticSource.Lines.Single().Text == "本地版本" &&
        automaticSource.Availability is { HasLocal: true, HasOnline: false, OnlineChecked: false },
        "自动模式应先返回本地歌词，且不为探测在线状态额外联网");
    Assert(onlineLookupCalls == 0, "本地优先命中时不应产生隐式在线请求");

    var onlineSource = await switchService.GetLyricsAsync(
        switchStored,
        switchTrack,
        true,
        "localFirst",
        "online",
        CancellationToken.None);
    Assert(
        onlineSource.SelectedSource == "online" && onlineSource.Lines.Single().Text == "在线版本" &&
        onlineSource.Availability is
        {
            HasLocal: true,
            HasOnline: true,
            OnlineChecked: true,
            HasOnlineCache: true,
            OnlineSource: "示例歌词来源"
        },
        "显式在线模式应绕过本地优先级，同时报告两类歌词和缓存可用状态");
    Assert(onlineLookupCalls == 1, "首次显式在线切换只调用一次歌词插件");

    var localAfterOnline = await switchService.GetLyricsAsync(
        switchStored,
        switchTrack,
        true,
        "localFirst",
        "local",
        CancellationToken.None);
    Assert(
        localAfterOnline.SelectedSource == "local" && localAfterOnline.Lines.Single().Text == "本地版本" &&
        localAfterOnline.Availability is { HasOnlineCache: true, HasOnline: true, OnlineChecked: true },
        "切回本地歌词时应保留在线缓存可用状态，不需要重复请求");
    Assert(onlineLookupCalls == 1, "本地/已缓存在线歌词切换不应重复联网");

    var embeddedDirectory = Path.Combine(testDirectory, "内嵌歌词");
    Directory.CreateDirectory(embeddedDirectory);
    var embeddedAudioPath = Path.Combine(embeddedDirectory, "内嵌测试.flac");
    await File.WriteAllBytesAsync(embeddedAudioPath, BuildFlacWithLyrics("[00:01.00]内嵌版本"));
    var embeddedTrack = new TrackInfo(
        "embedded-test",
        "内嵌测试",
        "测试歌手",
        "测试专辑",
        "内嵌测试.flac",
        "FLAC",
        null,
        0,
        10,
        DateTime.Now);
    var embeddedResult = await service.GetLyricsAsync(
        new StoredTrack(embeddedAudioPath, DateTime.Now),
        embeddedTrack,
        false,
        "localFirst",
        "local",
        CancellationToken.None);
    Assert(
        embeddedResult.Source == "内嵌歌词" && embeddedResult.Lines.Single().Text == "内嵌版本",
        "没有同目录 LRC 时应读取 FLAC Vorbis Comment 的内嵌歌词");
    await File.WriteAllTextAsync(Path.ChangeExtension(embeddedAudioPath, ".lrc"), "[00:01.00]同目录版本");
    var sidecarBeforeEmbedded = await service.GetLyricsAsync(
        new StoredTrack(embeddedAudioPath, DateTime.Now),
        embeddedTrack,
        false,
        "localFirst",
        "local",
        CancellationToken.None);
    Assert(
        sidecarBeforeEmbedded.Source == "本地 LRC" && sidecarBeforeEmbedded.Lines.Single().Text == "同目录版本",
        "同目录匹配 LRC 应优先于音频内嵌歌词");

    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var cp936 = Encoding.GetEncoding(936);
    var legacyDirectory = Path.Combine(testDirectory, "GBK歌词");
    Directory.CreateDirectory(legacyDirectory);
    var legacyAudioPath = Path.Combine(legacyDirectory, "清晨测试.flac");
    var legacyLyricPath = Path.ChangeExtension(legacyAudioPath, ".lrc");
    await File.WriteAllBytesAsync(legacyAudioPath, []);
    await File.WriteAllBytesAsync(
        legacyLyricPath,
        cp936.GetBytes("[00:01.00]清晨测试\r\n[00:02.00]晚风与星光"));
    var legacyTrack = new TrackInfo(
        "cp936-sidecar",
        "清晨测试",
        "测试歌手Example",
        "自下而上生",
        "清晨测试.flac",
        "FLAC",
        null,
        0,
        10,
        DateTime.Now);
    var legacyResult = await service.GetLyricsAsync(
        new StoredTrack(legacyAudioPath, DateTime.Now),
        legacyTrack,
        false,
        "localFirst",
        "local",
        CancellationToken.None);
    Assert(
        legacyResult.Source == "本地 LRC" &&
        legacyResult.Lines.Select(line => line.Text).SequenceEqual(new[] { "清晨测试", "晚风与星光" }),
        "无 BOM 的 CP936/GBK 同目录 LRC 应回退解码为正确中文，而不是 UTF-8 乱码");

    var legacyOverridePath = Path.Combine(legacyDirectory, "手动指定-GBK.lrc");
    await File.WriteAllBytesAsync(
        legacyOverridePath,
        cp936.GetBytes("[00:00.50]手动指定歌词\r\n[00:03.00]中文回归测试"));
    await service.SetOverrideAsync(legacyTrack.Id, legacyOverridePath);
    var legacyOverrideResult = await service.GetLyricsAsync(
        new StoredTrack(legacyAudioPath, DateTime.Now),
        legacyTrack,
        false,
        "localFirst",
        "local",
        CancellationToken.None);
    Assert(
        legacyOverrideResult.Source == "本地歌词 · 已指定" &&
        legacyOverrideResult.Lines.Select(line => line.Text).SequenceEqual(new[] { "手动指定歌词", "中文回归测试" }),
        "逐歌曲指定的 CP936/GBK LRC 也必须使用统一的本地歌词解码器");
    await service.RemoveOverrideAsync(legacyTrack.Id);

    var overridePath = Path.Combine(testDirectory, "手动选择.lrc");
    await File.WriteAllTextAsync(overridePath, "[00:00.20]指定歌词");
    await service.SetOverrideAsync(track.Id, overridePath);
    var overrideResult = await service.GetLyricsAsync(stored, track, false, "localFirst", CancellationToken.None);
    Assert(overrideResult.Source == "本地歌词 · 已指定" && overrideResult.Lines.Single().Text == "指定歌词", "逐歌曲指定歌词必须拥有最高优先级");
    var overrideInfo = await service.GetCacheInfoAsync(track.Id);
    Assert(overrideInfo.HasOverride, "逐歌曲缓存索引应标记指定歌词");
    await service.RemoveOverrideAsync(track.Id);
    File.Delete(lrcPath);

    var missingTrack = track with { Id = "missing-local-test" };
    var missingResult = await service.GetLyricsAsync(
        new StoredTrack(Path.Combine(testDirectory, "不存在.flac"), DateTime.Now),
        missingTrack,
        false,
        CancellationToken.None);
    Assert(missingResult.Lines.Count == 0 && missingResult.Source == "本地", "关闭在线歌词时不得发起外部回退");

    var streamRequestIndex = 0;
    var streamHandler = new StubHandler(request =>
    {
        streamRequestIndex += 1;
        if (streamRequestIndex == 1)
        {
            Assert(request.RequestUri?.Host == "stream.example", "首个音频请求必须发往 lease 原始来源");
            Assert(request.Headers.TryGetValues("X-Playback-Key", out var values) && values.Single() == "opaque-test-key",
                "同源首个请求必须携带后端 lease 请求头");
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("https://cdn.example/audio.mp3");
            return redirect;
        }

        Assert(request.RequestUri?.Host == "cdn.example", "音频流应跟随已验证的 HTTPS 跳转");
        Assert(!request.Headers.Contains("X-Playback-Key"), "跨源跳转不得转发敏感 lease 请求头");
        if (request.RequestUri?.AbsolutePath == "/direct.mp3" && streamRequestIndex == 3)
            Assert(request.Headers.Range?.ToString() == "bytes=0-", "宿主传输应请求完整音频范围");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4, 5])
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        return response;
    });
    var streamCache = Path.Combine(testDirectory, "stream-cache");
    await using (var streamService = new OnlinePlaybackSourceService(streamHandler, streamCache))
    {
        var quality = new PlatformAudioQuality("standard", "标准", 128, "mp3");
        var lease = new PlatformStreamLease(
            new Uri("https://stream.example/start"),
            DateTimeOffset.UtcNow.AddMinutes(5),
            "audio/mpeg",
            quality,
            new Dictionary<string, string> { ["X-Playback-Key"] = "opaque-test-key" });
        var prepared = await streamService.PrepareAsync(lease, "tx:test-track:standard", CancellationToken.None);
        Assert(prepared.Source.IsFile && File.Exists(prepared.Source.LocalPath), "带请求头的音频必须在原生后端形成临时播放源");
        Assert((await File.ReadAllBytesAsync(prepared.Source.LocalPath)).SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }),
            "原生临时播放源必须包含完整的受限响应数据");
        await streamService.ActivateAsync(prepared);

        var directLease = new PlatformStreamLease(
            new Uri("https://cdn.example/direct.mp3"),
            DateTimeOffset.UtcNow.AddMinutes(5),
            "audio/mpeg",
            quality);
        var direct = await streamService.PrepareAsync(directLease, CancellationToken.None);
        await streamService.ActivateAsync(direct);
        Assert(direct.Source == directLease.Url, "无请求头 lease 应直接交给原生播放器，不经过 WebView");
        Assert(File.Exists(prepared.Source.LocalPath), "切换播放源后应保留受限音频的有界会话缓存，避免重复整首下载");

        var hostLease = directLease with { UseHostTransport = true };
        var hosted = await streamService.PrepareAsync(hostLease, "fixture:host-transport", CancellationToken.None);
        Assert(hosted.Source.IsFile && (await File.ReadAllBytesAsync(hosted.Source.LocalPath)).Length == 5,
            "要求宿主传输的音频即使没有自定义请求头，也必须完整准备，不能直接交给解码器下载");
        var requestsAfterHosted = streamHandler.RequestCount;
        Assert((await streamService.PrepareAsync(hostLease, "fixture:host-transport", CancellationToken.None)).Source == hosted.Source
               && streamHandler.RequestCount == requestsAfterHosted,
            "宿主传输音频应复用有界缓存，不能重复下载");

        var reused = await streamService.PrepareAsync(lease, "tx:test-track:standard", CancellationToken.None);
        Assert(reused.Source == prepared.Source && streamHandler.RequestCount == requestsAfterHosted,
            "同一首受限在线歌曲再次播放时应直接复用已验证缓存，不重复请求网络");
        await streamService.DiscardPreparedAsync(reused);

        var warmed = await streamService.PrepareAsync(directLease, "next:standard", CancellationToken.None, bufferRemote: true);
        Assert(warmed.Source.IsFile && File.Exists(warmed.Source.LocalPath), "下一首预缓存必须完整准备无请求头的在线音频");
        var requestsAfterWarmup = streamHandler.RequestCount;
        var warmedAgain = await streamService.PrepareAsync(directLease, "next:standard", CancellationToken.None, bufferRemote: true);
        Assert(warmedAgain.Source == warmed.Source && streamHandler.RequestCount == requestsAfterWarmup, "重复的下一首预缓存不应再次下载");
        await streamService.PrepareAsync(lease, "tx:test-track:standard", CancellationToken.None);
        await streamService.ActivateAsync(warmed);
        Assert(File.Exists(warmed.Source.LocalPath), "其他待准备源不得使已完成的预缓存无法激活");

        var rejectedHttp = false;
        try
        {
            await streamService.PrepareAsync(
                new PlatformStreamLease(
                    new Uri("http://music.example/insecure.mp3"),
                    DateTimeOffset.UtcNow.AddMinutes(5),
                    "audio/mpeg",
                    quality),
                CancellationToken.None);
        }
        catch (OnlinePlaybackException)
        {
            rejectedHttp = true;
        }

        Assert(rejectedHttp, "非 loopback 的明文 HTTP 音频 lease 必须在发网前拒绝");
    }

    var ownershipCache = Path.Combine(testDirectory, "transport-ownership");
    await using (var owner = new OnlinePlaybackSourceService(new StubHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) }), ownershipCache))
    {
        PlatformStreamLease OwnedLease(string id) => new(new Uri("https://fixture.example/" + id),
            DateTimeOffset.UtcNow.AddMinutes(5), "audio/mpeg", new PlatformAudioQuality("test", "Test")) { UseHostTransport = true };
        var activePin = await owner.PrepareAsync(OwnedLease("active"), default);
        await owner.ActivateAsync(activePin);
        var nextPin = await owner.PrepareAsync(OwnedLease("next"), default);
        for (var i = 0; i < 12; i++)
        {
            await using var discarded = await owner.PrepareAsync(OwnedLease("pressure-" + i), default);
        }
        Assert(File.Exists(activePin.Source.LocalPath) && File.Exists(nextPin.Source.LocalPath),
            "当前音频与预加载必须独立持有资源，准备其他文件不得覆盖保护");
        await owner.ActivateAsync(nextPin);
        var videoReturnPin = await owner.PrepareAsync(OwnedLease("next"), default);
        await owner.ReleaseAsync();
        for (var i = 0; i < 12; i++)
        {
            await using var discarded = await owner.PrepareAsync(OwnedLease("later-" + i), default);
        }
        Assert(!File.Exists(activePin.Source.LocalPath) && File.Exists(videoReturnPin.Source.LocalPath),
            "替换后旧音频可淘汰，相同文件的另一独立持有者仍能返回音频");
        await owner.DiscardPreparedAsync(videoReturnPin);
        await owner.DiscardPreparedAsync(videoReturnPin);
    }
    var remainingOwnedFiles = Directory.EnumerateFiles(ownershipCache).ToArray();
    // Capture the first failed snapshot, without deleting/retrying until it passes. These are only
    // generated fixture filenames, never user media or credentials. A later disappearance does not
    // turn this assertion into success; share/HResult diagnostics help separate cleanup from locking.
    var ownershipDiagnostics = remainingOwnedFiles.Select(path =>
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return Path.GetFileName(path) + ":exclusive-read-ok:length=" + probe.Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Path.GetFileName(path) + ":" + error.GetType().Name + ":0x" + error.HResult.ToString("X8"); }
    }).ToArray();
    Assert(remainingOwnedFiles.Length == 0, "适配层关闭必须清理其资源；count=" + remainingOwnedFiles.Length +
        "; files=" + string.Join(",", ownershipDiagnostics));

    await using (var budgetAdapter = new OnlinePlaybackSourceService(new Auralis.MediaTransport.HttpMediaTransportSession(
        Path.Combine(testDirectory,"adapter-budget"), new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1,2,3,4]) }), budget:new Auralis.MediaTransport.MediaTransferBudget(1,1,1,1))))
    {
        try
        {
            await budgetAdapter.PrepareAsync(new PlatformStreamLease(new Uri("https://fixture.example/budget"),null,
                "audio/mpeg",new PlatformAudioQuality("test","Test")) { UseHostTransport=true }, default);
            throw new Exception("Missing adapter budget failure");
        }
        catch (OnlinePlaybackException error)
        {
            Assert(error.Message == "正在准备的媒体过多，请稍后重试。当前播放不会中断。",
                "预算错误须映射固定中文文案，不泄露后台 URI 或直接抛出传输异常");
        }
    }

    var videoHandler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/primary"
        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
    await using (var videoService = new OnlinePlaybackSourceService(videoHandler, Path.Combine(testDirectory, "video-cdn-cache")))
    {
        var videoLease = new PlatformVideoLease(new Uri("https://media.example/primary"), DateTimeOffset.UtcNow.AddMinutes(4), "video/mp4")
        { UseHostTransport = true, AlternateUrls = [new Uri("https://media.example/backup")] };
        var file = await videoService.PrepareVideoAsync(videoLease, default);
        Assert(file.Source.IsFile && videoHandler.RequestCount == 2, "视频主地址失败时尝试插件提供的备用地址");
        using var cancelledVideo = new CancellationTokenSource(); cancelledVideo.Cancel();
        try { await videoService.PrepareVideoAsync(videoLease, cancelledVideo.Token); throw new Exception("Cancelled video retried"); }
        catch (OperationCanceledException) { }
        Assert(videoHandler.RequestCount == 2, "取消视频不得继续请求备用地址");
    }

    foreach (var completeRange in new[] { true, false })
    {
        var rangeHandler = new StubHandler(request =>
        {
            Assert(request.Headers.Range?.ToString() == "bytes=0-", "宿主下载使用从头到尾的范围请求");
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([1, 2, 3, 4, 5])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 4, completeRange ? 5 : 10);
            return response;
        });
        var rangeCache = Path.Combine(testDirectory, "range-cache-" + completeRange);
        await using var rangeService = new OnlinePlaybackSourceService(rangeHandler, rangeCache);
        var partialRejected = false;
        try
        {
            var source = await rangeService.PrepareAsync(new PlatformStreamLease(
                new Uri("https://cdn.example/range.webm"), DateTimeOffset.UtcNow.AddMinutes(5), "audio/webm",
                new PlatformAudioQuality("opus", "Opus")) { UseHostTransport = true }, CancellationToken.None);
            Assert(completeRange && source.Source.IsFile, "只有完整范围响应可以交给播放器");
        }
        catch (OnlinePlaybackException) { partialRejected = true; }
        Assert(partialRejected != completeRange, "不完整的 206 片段不得伪装为完整歌曲");
        if (!completeRange)
            Assert(!Directory.EnumerateFiles(rangeCache).Any(), "失败片段必须清理，不能污染缓存");
    }

    var unsafeRedirectHandler = new StubHandler(_ =>
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("http://untrusted.example/audio.mp3");
        return redirect;
    });
    await using (var streamService = new OnlinePlaybackSourceService(
                     unsafeRedirectHandler,
                     Path.Combine(testDirectory, "unsafe-stream-cache")))
    {
        var redirectRejected = false;
        try
        {
            await streamService.PrepareAsync(
                new PlatformStreamLease(
                    new Uri("https://stream.example/redirect"),
                    DateTimeOffset.UtcNow.AddMinutes(5),
                    "audio/mpeg",
                    new PlatformAudioQuality("standard", "标准"),
                    new Dictionary<string, string> { ["X-Playback-Key"] = "opaque-test-key" }),
                CancellationToken.None);
        }
        catch (OnlinePlaybackException)
        {
            redirectRejected = true;
        }

        Assert(redirectRejected && unsafeRedirectHandler.RequestCount == 1,
            "不安全的跨源降级跳转必须在第二个 HTTP 请求前拒绝");
    }

    var executablePath = Path.Combine("C:\\", "Program Files", "Auralis Stable", "Auralis.exe");
    var registrationPlan = DefaultMusicAppRegistrationService.BuildRegistrationPlan(executablePath);
    Assert(registrationPlan.ExecutablePath == Path.GetFullPath(executablePath), "文件关联计划必须固定到绝对可执行文件路径");
    Assert(
        registrationPlan.OpenCommand == $"\"{Path.GetFullPath(executablePath)}\" --open \"%1\"",
        "shell open 命令必须分别引用可执行文件和 Windows 文件占位符");
    Assert(
        registrationPlan.Values
            .Select(value => $"{value.KeyPath}\u001f{value.ValueName ?? "(Default)"}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == registrationPlan.Values.Count,
        "注册计划不能对同一注册表值发出相互覆盖的写入");
    Assert(
        registrationPlan.Values.All(value =>
            !value.KeyPath.Contains("UserChoice", StringComparison.OrdinalIgnoreCase)),
        "候选应用注册不得触碰受 Windows 保护的 UserChoice");

    var openWithValues = registrationPlan.Values.Where(value =>
        value.KeyPath.EndsWith("\\OpenWithProgids", StringComparison.OrdinalIgnoreCase) &&
        value.ValueName == DefaultMusicAppRegistrationService.ProgId).ToArray();
    Assert(
        openWithValues.Length == DefaultMusicAppRegistrationService.SupportedExtensions.Count &&
        openWithValues.All(value => value.ValueKind == RegistryValueKind.None &&
                                    value.Value is byte[] bytes && bytes.Length == 0),
        "每个支持格式都必须通过空 REG_NONE 值加入 OpenWithProgids");

    foreach (var extension in DefaultMusicAppRegistrationService.SupportedExtensions)
    {
        Assert(
            registrationPlan.Values.Any(value =>
                value.KeyPath == $@"Software\Auralis\Capabilities\FileAssociations" &&
                value.ValueName == extension &&
                Equals(value.Value, DefaultMusicAppRegistrationService.ProgId)),
            $"Capabilities 必须声明 {extension}");
        Assert(
            registrationPlan.Values.Any(value =>
                value.KeyPath == @"Software\Classes\Applications\Auralis.exe\SupportedTypes" &&
                value.ValueName == extension),
            $"Open With 应用注册必须声明 {extension}");
        Assert(
            !registrationPlan.Values.Any(value =>
                value.KeyPath == $@"Software\Classes\{extension}" && value.ValueName is null),
            $"注册计划不得强写 {extension} 的默认 ProgID");
    }

    Assert(
        registrationPlan.Values.Any(value =>
            value.KeyPath == @"Software\RegisteredApplications" &&
            value.ValueName == DefaultMusicAppRegistrationService.RegisteredApplicationName &&
            Equals(value.Value, DefaultMusicAppRegistrationService.CapabilitiesPath)),
        "Auralis 必须通过 RegisteredApplications 指向自己的 Capabilities");
    Assert(
        DefaultMusicAppRegistrationService.PlannedValueEquals(
            RegistryValueKind.String,
            "Auralis",
            new RegistryValuePlan("test", "name", "Auralis", RegistryValueKind.String)) &&
        !DefaultMusicAppRegistrationService.PlannedValueEquals(
            RegistryValueKind.ExpandString,
            "Auralis",
            new RegistryValuePlan("test", "name", "Auralis", RegistryValueKind.String)),
        "幂等比较必须同时校验注册表类型和值");
    Assert(
        DefaultMusicAppRegistrationService.PlannedValueEquals(
            RegistryValueKind.None,
            Array.Empty<byte>(),
            new RegistryValuePlan("test", "name", Array.Empty<byte>(), RegistryValueKind.None)),
        "幂等比较必须正确识别既有空 REG_NONE 值");

    var unregistrationPlan = DefaultMusicAppRegistrationService.BuildUnregistrationPlan();
    Assert(
        unregistrationPlan.ValueDeletions.Count == DefaultMusicAppRegistrationService.SupportedExtensions.Count + 1,
        "撤销计划必须移除全部 OpenWithProgids 值和 RegisteredApplications 值");
    Assert(
        unregistrationPlan.ValueDeletions.Single(deletion =>
            deletion.KeyPath == @"Software\RegisteredApplications").ExpectedStringValue ==
        DefaultMusicAppRegistrationService.CapabilitiesPath,
        "撤销 RegisteredApplications 前必须验证它仍指向 Auralis");
    Assert(
        unregistrationPlan.OwnedTrees.Count == 3 &&
        unregistrationPlan.OwnedTrees.All(tree => registrationPlan.Values.Any(value =>
            value.KeyPath == tree.KeyPath &&
            value.ValueName == RegistrationPlan.OwnerMarkerName &&
            Equals(value.Value, RegistrationPlan.OwnerMarkerValue))),
        "撤销计划只能删除带 Auralis 所有权标记的注册树");

    var shellAudioPath = Path.Combine(testDirectory, "Shell 激活测试.flac");
    await File.WriteAllBytesAsync(shellAudioPath, []);
    var shellActivation = DefaultMusicAppRegistrationService.ParseShellActivation(["--open", shellAudioPath]);
    Assert(
        shellActivation.MaintenanceAction == ShellMaintenanceAction.None &&
        shellActivation.AudioFilePath == Path.GetFullPath(shellAudioPath),
        "shell --open 必须只接受存在且受支持的本地音频文件");
    Assert(
        DefaultMusicAppRegistrationService.ParseShellActivation([shellAudioPath]).AudioFilePath ==
        Path.GetFullPath(shellAudioPath),
        "兼容调用必须允许单个裸文件参数");
    Assert(
        DefaultMusicAppRegistrationService.ParseShellActivation(["--register-file-associations"]).MaintenanceAction ==
        ShellMaintenanceAction.Register &&
        DefaultMusicAppRegistrationService.ParseShellActivation(["--unregister-file-associations"]).MaintenanceAction ==
        ShellMaintenanceAction.Unregister &&
        DefaultMusicAppRegistrationService.ParseShellActivation(["--default-apps-settings"]).MaintenanceAction ==
        ShellMaintenanceAction.OpenSettings,
        "维护命令解析必须区分登记、撤销和打开系统设置");
    Assert(
        DefaultMusicAppRegistrationService.ParseShellActivation([Path.Combine(testDirectory, "missing.mp3")])
            .AudioFilePath is null,
        "shell 激活不得接受不存在的路径");
}
catch (Exception error)
{
    mediaFixtureFailed = true;
    Console.Error.WriteLine("FAIL media fixture before cleanup: " + error);
}
finally
{
    try { Directory.Delete(testDirectory, recursive: true); }
    catch (IOException error)
    {
        mediaFixtureFailed = true;
        Console.Error.WriteLine("FAIL media fixture cleanup (original failure preserved): " + error.Message);
    }
}
if (mediaFixtureFailed) { Environment.ExitCode = 1; return; }

var logTestDirectory = Path.Combine(Path.GetTempPath(), "AuralisLogTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(logTestDirectory);
var staleLog = Path.Combine(logTestDirectory, "auralis-20000101-000000-000-000001-000.jsonl");
await File.WriteAllTextAsync(staleLog, "stale");
File.SetLastWriteTimeUtc(staleLog, DateTime.UtcNow.AddDays(-30));
var logOptions = new AppLogOptions(
    logTestDirectory,
    MaximumFileBytes: 1600,
    MaximumFileCount: 3,
    RetentionDays: 14,
    QueueCapacity: 4096);
var testLogger = new RollingFileAppLogger(logOptions);
try
{
    var producers = Enumerable.Range(0, 8).Select(producer => Task.Run(() =>
    {
        for (var index = 0; index < 25; index++)
        {
            testLogger.Log(
                AppLogLevel.Debug,
                "tests.concurrent",
                "write",
                new Dictionary<string, object?>
                {
                    [AppLogFieldNames.Count] = (producer * 25) + index,
                    [AppLogFieldNames.Message] = new string('x', 180)
                });
        }
    }));
    await Task.WhenAll(producers);

    testLogger.Log(
        AppLogLevel.Warning,
        "Tests / Privacy",
        "Sensitive Input",
        new Dictionary<string, object?>
        {
            [AppLogFieldNames.Message] =
                "GET https://music.example.test/stream?id=42&signature=signature-value\n" +
                "Authorization: Bearer bearer-value\nCookie: session=cookie-value\n" +
                "token=token-value password=pass-value credential=credential-value\n" +
                @"file C:\Users\Alice\Music\private-song.flac",
            [AppLogFieldNames.Status] = "safe-status",
            ["rawPayload"] = "must-not-be-written"
        },
        new InvalidOperationException(
            @"exception-secret C:\Users\Alice\private.txt token=exception-token"));

    Assert(await testLogger.FlushAsync(TimeSpan.FromSeconds(5)), "日志刷新应在有限时间内完成");
    Assert(await testLogger.StopAsync(TimeSpan.FromSeconds(5)), "日志后台写入器应在有限时间内停止");

    var logFiles = Directory.GetFiles(logTestDirectory, "auralis-*.jsonl");
    Assert(logFiles.Length is > 0 and <= 3, "滚动日志必须保留至少一份且不超过配置的文件数量");
    Assert(!File.Exists(staleLog), "超过十四天的日志必须在后台清理");
    Assert(
        logFiles.All(path => new FileInfo(path).Length <= logOptions.MaximumFileBytes),
        "单条记录小于上限时，滚动日志文件不得超过配置大小");

    var persistedLog = string.Join("\n", await Task.WhenAll(logFiles.Select(path => File.ReadAllTextAsync(path))));
    Assert(persistedLog.Contains("safe-status", StringComparison.Ordinal), "允许的结构化字段必须写入日志");
    Assert(!persistedLog.Contains("must-not-be-written", StringComparison.Ordinal), "非白名单字段不得进入日志");
    Assert(!persistedLog.Contains("signature-value", StringComparison.Ordinal) &&
           !persistedLog.Contains("bearer-value", StringComparison.Ordinal) &&
           !persistedLog.Contains("cookie-value", StringComparison.Ordinal) &&
           !persistedLog.Contains("token-value", StringComparison.Ordinal) &&
           !persistedLog.Contains("pass-value", StringComparison.Ordinal) &&
           !persistedLog.Contains("credential-value", StringComparison.Ordinal),
        "URL 查询、授权头、Cookie 和凭据字段必须脱敏");
    Assert(!persistedLog.Contains(@"C:\Users\Alice", StringComparison.OrdinalIgnoreCase),
        "Windows 绝对路径不得进入日志");
    Assert(!persistedLog.Contains("exception-secret", StringComparison.Ordinal) &&
           !persistedLog.Contains("exception-token", StringComparison.Ordinal) &&
           persistedLog.Contains("System.InvalidOperationException", StringComparison.Ordinal) &&
           persistedLog.Contains("hresult", StringComparison.Ordinal),
        "异常只能保留类型和 HRESULT，不得写入消息或堆栈");

    foreach (var line in persistedLog.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        using var document = System.Text.Json.JsonDocument.Parse(line);
        Assert(document.RootElement.TryGetProperty("eventId", out _), "每一行日志都必须是带稳定事件 ID 的 JSON");
        Assert(document.RootElement.TryGetProperty("component", out _), "每一行日志都必须包含组件标识");
    }
}
finally
{
    await testLogger.DisposeAsync();
    Directory.Delete(logTestDirectory, recursive: true);
}

var defaultLogOptions = new AppLogOptions("unused");
Assert(defaultLogOptions.MaximumFileBytes == 4L * 1024L * 1024L &&
       defaultLogOptions.MaximumFileCount == 5 &&
       defaultLogOptions.RetentionDays == 14,
    "应用日志默认必须按约 4 MiB、最多五份、保留十四天滚动");

var unavailableLogRoot = Path.Combine(Path.GetTempPath(), "AuralisUnavailableLogTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(unavailableLogRoot);
var fileInsteadOfDirectory = Path.Combine(unavailableLogRoot, "not-a-directory");
await File.WriteAllTextAsync(fileInsteadOfDirectory, "occupied");
var unavailableLogger = new RollingFileAppLogger(new AppLogOptions(fileInsteadOfDirectory, QueueCapacity: 8));
try
{
    for (var index = 0; index < 100; index++)
    {
        unavailableLogger.Log(AppLogLevel.Information, "tests.failure", "directory-unavailable");
    }

    Assert(!await unavailableLogger.FlushAsync(TimeSpan.FromSeconds(2)),
        "日志目录不可用时应安全降级并报告未写入，而不是让应用失败");
    Assert(await unavailableLogger.StopAsync(TimeSpan.FromSeconds(2)),
        "日志目录不可用时后台写入器仍必须可在有限时间内停止");
}
finally
{
    await unavailableLogger.DisposeAsync();
    Directory.Delete(unavailableLogRoot, recursive: true);
}

var windowSettingsTestDirectory = Path.Combine(
    Path.GetTempPath(),
    "AuralisWindowSettingsTests",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(windowSettingsTestDirectory);
try
{
    var windowSettingsPath = Path.Combine(windowSettingsTestDirectory, "window-settings.json");
    await File.WriteAllTextAsync(windowSettingsPath, "{\"closeToTray\":true}");
    var windowSettingsStore = new WindowSettingsStore(windowSettingsPath);
    var migratedSettings = windowSettingsStore.Load();
    Assert(migratedSettings.CloseToTray, "旧版窗口设置升级时必须保留关闭到托盘选项");
    Assert(migratedSettings.UiLanguage == UiLanguagePreference.System,
        "旧版窗口设置没有语言字段时必须安全迁移为跟随系统");

    windowSettingsStore.Save(new NativeWindowSettings(1, true, UiLanguagePreference.EnglishUnitedStates));
    var restoredWindowSettings = windowSettingsStore.Load();
    Assert(restoredWindowSettings.SchemaVersion == WindowSettingsStore.CurrentSchemaVersion &&
           restoredWindowSettings.CloseToTray &&
           restoredWindowSettings.UiLanguage == UiLanguagePreference.EnglishUnitedStates,
        "窗口设置必须原子持久化 schema、托盘语义和界面语言");
    Assert(!Directory.EnumerateFiles(windowSettingsTestDirectory, "window-settings.json.*.tmp").Any(),
        "窗口设置成功保存后不得遗留写入器专属临时文件");

    var chineseSystemLanguage = UiLanguagePreference.Resolve(
        UiLanguagePreference.System,
        System.Globalization.CultureInfo.GetCultureInfo("zh-Hans-CN"));
    var englishSystemLanguage = UiLanguagePreference.Resolve(
        UiLanguagePreference.System,
        System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
    Assert(chineseSystemLanguage.ResolvedLanguage == UiLanguagePreference.SimplifiedChinese,
        "中文 Windows 必须把 system 语言解析为 zh-CN");
    Assert(englishSystemLanguage.ResolvedLanguage == UiLanguagePreference.EnglishUnitedStates,
        "首版未覆盖的系统语言必须使用 en-US 安全回退");
    Assert(UiLanguagePreference.Normalize("invalid") == UiLanguagePreference.System,
        "损坏或未知语言值必须回退到 system");
    Assert(
        Auralis.Localization.NativeText.Get(UiLanguagePreference.EnglishUnitedStates, "tray.exit") == "Exit" &&
        Auralis.Localization.NativeText.Get(UiLanguagePreference.SimplifiedChinese, "tray.exit") == "退出",
        "Native 启动壳和托盘的中英文资源必须来自同一稳定 key");

    await File.WriteAllTextAsync(windowSettingsPath, "{not-json");
    var recoveredSettings = windowSettingsStore.Load();
    Assert(recoveredSettings == NativeWindowSettings.Default,
        "损坏的窗口设置不得阻止启动，必须恢复安全默认值");

    await File.WriteAllTextAsync(windowSettingsPath, "[]");
    Assert(windowSettingsStore.Load() == NativeWindowSettings.Default,
        "根节点不是对象的有效 JSON 不得阻止启动，必须恢复安全默认值");

    await File.WriteAllTextAsync(windowSettingsPath, "{\"uiLanguage\":123}");
    Assert(windowSettingsStore.Load() == NativeWindowSettings.Default,
        "语言字段类型错误时不得阻止启动，必须恢复安全默认值");
}
finally
{
    Directory.Delete(windowSettingsTestDirectory, recursive: true);
}

Console.WriteLine("Auralis service tests passed: library, lyrics, playback safety, registration, native language settings, and bounded privacy-safe logging verified.");


static async Task RunLanPreviewAsync()
{
    var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "Auralis", "wwwroot", "lan");
    if (!File.Exists(Path.Combine(webRoot, "index.html")))
    {
        throw new DirectoryNotFoundException("Run --lan-preview from the repository root.");
    }

    var previewRoot = Path.Combine(Path.GetTempPath(), "AuralisLanPreview", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(previewRoot);
    var logRoot = Path.Combine(previewRoot, "logs");
    var logger = new RollingFileAppLogger(new AppLogOptions(logRoot));
    try
    {
        var examples = new[]
        {
            (File: "ocean.wav", Title: "海风与旋律", Artist: "Auralis", Album: "合成音频测试", Frequency: 440d),
            (File: "stars.wav", Title: "你是星辰", Artist: "Auralis 测试音乐库", Album: "本地音乐", Frequency: 523.25d),
            (File: "morning.wav", Title: "清晨的风", Artist: "Auralis 测试音乐库", Album: "本地音乐", Frequency: 659.25d)
        };
        var tracks = new List<LanSharedTrack>();
        foreach (var example in examples)
        {
            var path = Path.Combine(previewRoot, example.File);
            await File.WriteAllBytesAsync(path, BuildPreviewWave(example.Frequency, 4));
            tracks.Add(new LanSharedTrack(
                example.File,
                path,
                example.Title,
                example.Artist,
                example.Album,
                ".wav",
                new FileInfo(path).Length,
                4,
                DateTime.UtcNow,
                null));
        }

        var port = ReserveLoopbackPort();
        await using var service = new LanMusicSharingService(logger, webRoot, () => []);
        service.ReplaceLibrary(tracks);
        await service.ApplySettingsAsync(new LanSharingSettings(1, true, port));
        var url = service.CurrentState.PairingUrls.Single();
        Console.WriteLine("Auralis LAN preview ready:");
        Console.WriteLine(url);
        Console.WriteLine("Press Ctrl+C to stop.");

        using var stopped = new CancellationTokenSource();
        Console.CancelKeyPress += OnCancel;
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stopped.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }

        void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            stopped.Cancel();
        }
    }
    finally
    {
        await logger.DisposeAsync();
        Directory.Delete(previewRoot, recursive: true);
    }
}

static byte[] BuildPreviewWave(double frequency, int durationSeconds)
{
    const int sampleRate = 44_100;
    const short channels = 1;
    const short bitsPerSample = 16;
    var sampleCount = checked(sampleRate * durationSeconds);
    var dataLength = checked(sampleCount * channels * bitsPerSample / 8);
    using var output = new MemoryStream(44 + dataLength);
    using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
    writer.Write(Encoding.ASCII.GetBytes("RIFF"));
    writer.Write(36 + dataLength);
    writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
    writer.Write(16);
    writer.Write((short)1);
    writer.Write(channels);
    writer.Write(sampleRate);
    writer.Write(sampleRate * channels * bitsPerSample / 8);
    writer.Write((short)(channels * bitsPerSample / 8));
    writer.Write(bitsPerSample);
    writer.Write(Encoding.ASCII.GetBytes("data"));
    writer.Write(dataLength);
    for (var index = 0; index < sampleCount; index++)
    {
        var envelope = Math.Min(1d, Math.Min(index / 2_000d, (sampleCount - index) / 2_000d));
        var sample = Math.Sin(2 * Math.PI * frequency * index / sampleRate) * .12d * envelope;
        writer.Write((short)(sample * short.MaxValue));
    }
    writer.Flush();
    return output.ToArray();
}

static byte[] BuildFlacWithLyrics(string lyrics)
{
    using var output = new MemoryStream();
    using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
    writer.Write(Encoding.ASCII.GetBytes("fLaC"));

    writer.Write((byte)0x00); // STREAMINFO, not the final metadata block.
    writer.Write(new byte[] { 0x00, 0x00, 0x22 });
    writer.Write(new byte[34]);

    var comment = Encoding.UTF8.GetBytes("LYRICS=" + lyrics);
    using var commentBlock = new MemoryStream();
    using (var commentWriter = new BinaryWriter(commentBlock, Encoding.UTF8, leaveOpen: true))
    {
        commentWriter.Write(0u); // Empty vendor string.
        commentWriter.Write(1u);
        commentWriter.Write((uint)comment.Length);
        commentWriter.Write(comment);
    }

    var blockLength = checked((int)commentBlock.Length);
    writer.Write((byte)0x84); // VORBIS_COMMENT and final metadata block.
    writer.Write(new[]
    {
        (byte)(blockLength >> 16),
        (byte)(blockLength >> 8),
        (byte)blockLength
    });
    writer.Write(commentBlock.ToArray());
    writer.Flush();
    return output.ToArray();
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount += 1;
        return Task.FromResult(responder(request));
    }
}
