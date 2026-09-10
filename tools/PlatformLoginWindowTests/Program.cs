using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Auralis.Platform.Abstractions;
using Auralis.Platform.Host;

namespace Auralis;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] is not ("--run" or "--show"))) return 2;
        var show = args.Length == 0 || args[0] == "--show";
        var app = new App();
        app.Startup += async (_, _) =>
        {
            var window = new MainWindow(); app.MainWindow = window;
            new WindowInteropHelper(window).EnsureHandle();
            if (show) window.Show();
            try
            {
                var failures = await window.RunChecksAsync();
                if (!show) app.Shutdown(failures == 0 ? 0 : 1);
            }
            catch (Exception e) { Console.WriteLine("FAIL fixture: " + e.GetType().Name); app.Shutdown(1); }
        };
        return app.Run();
    }
}

// Not the production App: no logger, vault, WebView, profile, single-instance IPC or media integration.
public sealed class App : Application
{
    public FixtureBackend PlatformBackend { get; } = new();
    protected override void OnExit(ExitEventArgs e)
    {
        PlatformBackend.Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}

public sealed class FixtureBackend
{
    public FixtureServices Services { get; } = new();
    public PlatformPluginHost Host { get; }
    public PlatformRouter Router => Host.Router;
    public Task<PlatformPluginHostSnapshot> DiscoverAsync(CancellationToken token = default) => Host.DiscoverAsync(token);
    public FixtureBackend()
    {
        var root = Path.Combine(Path.GetTempPath(), "Auralis-login-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.Copy(typeof(LoginFixturePlugin).Assembly.Location, Path.Combine(root, "Fixture.dll"));
        File.WriteAllText(Path.Combine(root, "platform.plugin.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 5, id = "tests.login", displayName = "Login lifecycle", version = "1.0.0",
            minimumHostApiVersion = 1, maximumHostApiVersion = 1, entryAssembly = "Fixture.dll",
            entryType = typeof(LoginFixturePlugin).FullName,
            hostRequirements = new { minimumHostSdkVersion = "1.2.0", requiredFeatures = new[] { "native-login.v1", "comment-artwork.v1" } },
            providers = new[] {
                new { id = "fixture.window", displayName = "Fixture", capabilities = new[] { "Authentication", "NativeLogin" }, commentArtworkDomains = Array.Empty<string>() },
                new { id = "fixture.authonly", displayName = "Fixture", capabilities = new[] { "Authentication" }, commentArtworkDomains = Array.Empty<string>() }
            }
        }));
        // This diagnostic creates its own reviewed synthetic DLL. No imported/user package is trusted.
        Host = new(new PlatformPluginHostOptions([root], Services, minimumManifestSchemaVersion: 5));
    }
}

public partial class MainWindow : Window
{
    private bool _windowClosed;
    private bool _nativeDarkTheme => false;
    private readonly List<string> _messages = [];
    private int _refreshes;
    private Task ExecuteScriptAsync(string script)
    {
        var start = script.IndexOf('(') + 1;
        _messages.Add(JsonSerializer.Deserialize<string>(script[start..^1])!);
        return Task.CompletedTask;
    }
    private Task SendPlatformConfigurationAsync() { _refreshes++; return Task.CompletedTask; }
    public MainWindow()
    {
        Title = "Auralis 在线登录生命周期隔离验收 · 无真实账号";
        Width = 850; Height = 520;
        Content = new TextBlock { Text = "正在运行合成插件回归；不会打开认证网页或访问真实账户。", Margin = new Thickness(24) };
        Closed += (_, _) => { _windowClosed = true; ClosePlatformLogins(); };
    }
    internal async Task<int> RunChecksAsync()
    {
        var fixture = ((App)Application.Current).PlatformBackend.Services;
        var lines = new List<string>(); var failed = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        async Task Scenario(string name, Func<Task> test)
        {
            fixture.Options.Clear(); _messages.Clear(); _refreshes = 0;
            try { await test(); lines.Add("PASS " + name); }
            catch (Exception e) { failed++; lines.Add("FAIL " + name + ": " + e.Message); }
            finally { fixture.Options.Clear(); ClosePlatformLogins(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); }
        }
        await Scenario("窗口复用与后台 Closed 回调", async () =>
        {
            await OpenPlatformLoginAsync("fixture.window");
            Check(_platformLogins.Count == 1, "Session did not open");
            fixture.Options["background-close"] = "true";
            await OpenPlatformLoginAsync("fixture.window");
            Check(_platformLogins.Count == 1, "Background callback mutated UI-owned state synchronously");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(_platformLogins.Count == 0, "Closed callback not applied on dispatcher");
        });
        await Scenario("Show 与 Close 同时抛错后可以重试", async () =>
        {
            fixture.Options["throw-show"] = "true"; fixture.Options["throw-close"] = "true";
            await OpenPlatformLoginAsync("fixture.window");
            Check(_platformLogins.Count == 0 && _openingPlatformLogins.Count == 0, "Broken session retained");
            fixture.Options.Clear(); await OpenPlatformLoginAsync("fixture.window");
            Check(_platformLogins.Count == 1, "Retry failed");
        });
        await Scenario("Activate 抛错不逃出可选插件边界", async () =>
        {
            await OpenPlatformLoginAsync("fixture.window"); fixture.Options["throw-activate"] = "true";
            await OpenPlatformLoginAsync("fixture.window");
            Check(_platformLogins.Count == 0 && _messages.Count > 0, "Activation failure retained session or omitted UI error");
        });
        await Scenario("Close 抛错仍完成账号断开", async () =>
        {
            await OpenPlatformLoginAsync("fixture.window"); fixture.Options["throw-close"] = "true";
            fixture.Options["signed-out"] = "false";
            await SignOutPlatformAsync("fixture.window");
            Check(fixture.Options["signed-out"] == "true" && _refreshes == 1, "Close failure prevented sign-out");
        });
        await Scenario("没有 NativeLogin 能力的插件可以正常断开", async () =>
        {
            await SignOutPlatformAsync("fixture.authonly");
            Check(_refreshes == 1 && _messages.Any(x => x.Contains("已断开平台账号，本地音乐")), "Optional native capability treated as required");
        });
        await Scenario("成功登录自动关闭仍刷新账号", async () =>
        {
            fixture.Options["authenticate-close"] = "true";
            await OpenPlatformLoginAsync("fixture.window");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(_platformLogins.Count == 0 && _refreshes == 1, "Successful close lost account refresh");
        });
        await Scenario("旧窗口迟到 Closed 不移除新会话", async () =>
        {
            await OpenPlatformLoginAsync("fixture.window");
            var old = _platformLogins["fixture.window"]; old.Close();
            await OpenPlatformLoginAsync("fixture.window"); var replacement = _platformLogins["fixture.window"];
            fixture.Options["background-close"] = "true"; old.Activate();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(ReferenceEquals(_platformLogins["fixture.window"], replacement), "Stale close removed replacement");
        });
        await Scenario("断开操作使排队的认证刷新失效", async () =>
        {
            fixture.Options["authenticate-close"] = "true";
            await OpenPlatformLoginAsync("fixture.window");
            await SignOutPlatformAsync("fixture.window");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(_refreshes == 1, "Queued old authentication refreshed after sign-out");
        });
        await Scenario("插件取消回调抛错不阻止断开", async () =>
        {
            using var pending = new CancellationTokenSource();
            using var callback = pending.Token.Register(() => throw new InvalidOperationException("Synthetic cancellation failure"));
            _openingPlatformLogins["fixture.authonly"] = pending;
            await SignOutPlatformAsync("fixture.authonly");
            Check(pending.IsCancellationRequested && _openingPlatformLogins.Count == 0 && _refreshes == 1, "Cancellation failure blocked sign-out");
        });
        await Scenario("关闭主窗口清理所有损坏的可选会话", async () =>
        {
            await OpenPlatformLoginAsync("fixture.window"); fixture.Options["throw-close"] = "true";
            using var pending = new CancellationTokenSource();
            using var callback = pending.Token.Register(() => throw new InvalidOperationException("Synthetic cancellation failure"));
            _openingPlatformLogins["fixture.pending"] = pending;
            ClosePlatformLogins();
            Check(_platformLogins.Count == 0 && _openingPlatformLogins.Count == 0 && _platformLoginGenerations.Count == 0, "Shutdown retained optional sessions");
        });
        // Keep CLI status ASCII: GUI-subsystem processes may not have a console encoding handle.
        for (var i = 0; i < lines.Count; i++) Console.WriteLine($"Case {i + 1}: {(lines[i].StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : "FAIL")}");
        Console.WriteLine($"Result: {lines.Count - failed} passed, {failed} failed; real WPF dispatcher/production partial, synthetic providers only.");
        Content = new TextBlock { Text = "隔离验收：生产登录处理代码 + 合成插件，无真实账号/认证网页\n\n" + string.Join("\n\n", lines), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) };
        return failed;
    }
}

public sealed class FixtureServices : IPlatformHostContextFactory, IPlatformHttpClientFactory, IPlatformCredentialStore,
    IPlatformCache, IPlatformSettings, IPlatformLogger, IPlatformTimeProvider
{
    public ConcurrentDictionary<string, string> Options { get; } = new();
    public PlatformHostContext CreateContext(string id) => new(id, this, this, this, this, this, this);
    public HttpClient CreateClient(string name) => throw new InvalidOperationException("Fixture HTTP forbidden");
    ValueTask<PlatformCredential?> IPlatformCredentialStore.GetAsync(string key, CancellationToken token) => throw new InvalidOperationException("Fixture vault forbidden");
    public ValueTask DeleteAsync(string key, CancellationToken token) => throw new InvalidOperationException("Fixture vault forbidden");
    public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, DateTimeOffset? expiry, CancellationToken token) => throw new InvalidOperationException("Fixture persistence forbidden");
    ValueTask<PlatformCacheEntry?> IPlatformCache.GetAsync(string key, CancellationToken token) => ValueTask.FromResult<PlatformCacheEntry?>(null);
    public ValueTask RemoveAsync(string key, CancellationToken token) { Options[key] = "true"; return ValueTask.CompletedTask; }
    ValueTask<string?> IPlatformSettings.GetAsync(string key, CancellationToken token) => ValueTask.FromResult(Options.GetValueOrDefault(key));
    public void Log(PlatformLogLevel level, string name, string message, Exception? exception = null) { }
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken token) => new(Task.Delay(delay, token));
}
public sealed class LoginFixturePlugin : IAuralisPlatformPlugin
{
    public PlatformPluginDescriptor Descriptor { get; } = new("tests.login", "Login lifecycle", new(1, 0, 0), 1, 1);
    public ValueTask<PlatformResult<IReadOnlyList<IPlatformProvider>>> CreateProvidersAsync(PlatformHostContext context, CancellationToken token) =>
        ValueTask.FromResult(PlatformResult<IReadOnlyList<IPlatformProvider>>.Success([new LoginFixtureProvider(context, true), new LoginFixtureProvider(context, false)]));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class LoginFixtureProvider(PlatformHostContext context, bool native) : IPlatformProvider, IAuthenticationCapability, IPlatformNativeLoginCapability
{
    public PlatformProviderDescriptor Descriptor { get; } = new(native ? "fixture.window" : "fixture.authonly", "Fixture", new(1, 0, 0),
        native ? [PlatformCapabilityKind.Authentication, PlatformCapabilityKind.NativeLogin] : [PlatformCapabilityKind.Authentication]);
    public ValueTask<PlatformResult<PlatformUnit>> InitializeAsync(CancellationToken token) => ValueTask.FromResult(PlatformResult<PlatformUnit>.Success(PlatformUnit.Value));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<PlatformResult<PlatformAuthenticationState>> GetAuthenticationStateAsync(CancellationToken token) => Task.FromResult(PlatformResult<PlatformAuthenticationState>.Success(new() { Status = PlatformAuthenticationStatus.SignedOut }));
    public Task<PlatformResult<PlatformAuthenticationChallenge>> BeginAuthenticationAsync(PlatformAuthenticationRequest request, CancellationToken token) => throw new NotSupportedException();
    public Task<PlatformResult<PlatformAuthenticationState>> CompleteAuthenticationAsync(PlatformAuthenticationCompletion completion, CancellationToken token) => throw new NotSupportedException();
    public async Task<PlatformResult<PlatformUnit>> SignOutAsync(CancellationToken token) { await context.Cache.RemoveAsync("signed-out", token); return PlatformResult<PlatformUnit>.Success(PlatformUnit.Value); }
    public Task<PlatformResult<IPlatformNativeLoginSession>> CreateLoginSessionAsync(CancellationToken token) => Task.FromResult(PlatformResult<IPlatformNativeLoginSession>.Success(new LoginFixtureSession(context.Settings)));
    public Task<PlatformResult<PlatformUnit>> ClearLoginDataAsync(nint ownerWindow, CancellationToken token) => Task.FromResult(PlatformResult<PlatformUnit>.Success(PlatformUnit.Value));
}
public sealed class LoginFixtureSession(IPlatformSettings settings) : IPlatformNativeLoginSession
{
    public event EventHandler? Authenticated;
    public event EventHandler? Closed;
    private bool Is(string key) => settings.GetAsync(key, default).AsTask().GetAwaiter().GetResult() == "true";
    public void Show(nint ownerWindow, bool dark)
    {
        if (Is("throw-show")) throw new InvalidOperationException("Synthetic Show failure");
        if (Is("authenticate-close")) { Authenticated?.Invoke(this, EventArgs.Empty); Closed?.Invoke(this, EventArgs.Empty); }
    }
    public void Activate()
    {
        if (Is("throw-activate")) throw new InvalidOperationException("Synthetic Activate failure");
        if (Is("background-close")) Task.Run(() => Closed?.Invoke(this, EventArgs.Empty)).GetAwaiter().GetResult();
    }
    public void Close()
    {
        if (Is("throw-close")) throw new InvalidOperationException("Synthetic Close failure");
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
