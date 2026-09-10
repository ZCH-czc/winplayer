using System.Text.Json;
using System.Windows.Interop;
using Auralis.Platform.Abstractions;

namespace Auralis;

public partial class MainWindow
{
    private readonly Dictionary<string, IPlatformNativeLoginSession> _platformLogins = new();
    private readonly Dictionary<string, CancellationTokenSource> _openingPlatformLogins = new();
    private readonly Dictionary<string, object> _platformLoginGenerations = new();

    private async Task OpenPlatformLoginAsync(string providerId)
    {
        if (_platformLogins.TryGetValue(providerId, out var current))
        {
            try { current.Activate(); }
            catch
            {
                _platformLogins.Remove(providerId);
                _platformLoginGenerations.Remove(providerId);
                ClosePlatformLoginSession(current);
                if (!_windowClosed) await ShowPlatformLoginErrorAsync("登录插件无法激活窗口，请重试。");
            }
            return;
        }
        if (_openingPlatformLogins.ContainsKey(providerId) || _windowClosed) return;
        using var cancellation = new CancellationTokenSource();
        var generation = new object();
        _platformLoginGenerations[providerId] = generation;
        _openingPlatformLogins[providerId] = cancellation;
        try
        {
            var result = await ((App)System.Windows.Application.Current).PlatformBackend.Router
                .RouteAsync<IPlatformNativeLoginCapability, IPlatformNativeLoginSession>(providerId,
                    (capability, token) => capability.CreateLoginSessionAsync(token), cancellation.Token);
            if (_windowClosed || cancellation.IsCancellationRequested)
            {
                if (result.IsSuccess) ClosePlatformLoginSession(result.Value);
                return;
            }
            if (!result.IsSuccess)
            {
                await ShowPlatformLoginErrorAsync("对应平台登录插件未安装、版本不兼容或暂不可用。请检查插件后重试。");
                return;
            }
            var session = result.Value;
            _platformLogins[providerId] = session;
            session.Authenticated += (_, _) => Dispatcher.BeginInvoke(async () =>
            {
                // Auto-closing the successful login window must not drop its queued refresh.
                // A new login or explicit sign-out invalidates this generation.
                if (!_windowClosed && _platformLoginGenerations.TryGetValue(providerId, out var active) && ReferenceEquals(active, generation))
                    await SendPlatformConfigurationAsync();
            });
            session.Closed += (_, _) =>
            {
                void RemoveClosedSession()
                {
                    if (_platformLogins.TryGetValue(providerId, out var active) && ReferenceEquals(active, session))
                        _platformLogins.Remove(providerId);
                }
                // Plugins may signal completion on a worker. Window/session dictionaries belong
                // to the UI thread; an old callback must not remove a later replacement session.
                if (Dispatcher.CheckAccess()) RemoveClosedSession();
                else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke((Action)RemoveClosedSession);
            };
            try { session.Show(new WindowInteropHelper(this).Handle, _nativeDarkTheme); }
            catch
            {
                _platformLogins.Remove(providerId);
                _platformLoginGenerations.Remove(providerId);
                ClosePlatformLoginSession(session);
                throw;
            }
        }
        catch (OperationCanceledException) { }
        catch { if (!_windowClosed) await ShowPlatformLoginErrorAsync("登录插件无法打开窗口，请检查插件版本后重试。"); }
        finally
        {
            if (_openingPlatformLogins.TryGetValue(providerId, out var pending) && ReferenceEquals(pending, cancellation))
                _openingPlatformLogins.Remove(providerId);
        }
    }

    private async Task SignOutPlatformAsync(string providerId)
    {
        _platformLoginGenerations.Remove(providerId);
        if (_openingPlatformLogins.Remove(providerId, out var pending)) CancelPlatformLogin(pending);
        if (_platformLogins.Remove(providerId, out var session)) ClosePlatformLoginSession(session);
        var backend = ((App)System.Windows.Application.Current).PlatformBackend;
        var router = backend.Router;
        var ownerHandle = new WindowInteropHelper(this).Handle;
        var result = await router.SignOutAsync(providerId, CancellationToken.None);
        if (!result.IsSuccess) { await ShowPlatformLoginErrorAsync("未能断开平台账号，请检查插件后重试。"); return; }
        var provider = (await backend.DiscoverAsync()).Providers.FirstOrDefault(p =>
            p.Provider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        var cleared = provider is not null;
        if (provider?.Provider.Capabilities.Contains(PlatformCapabilityKind.NativeLogin) == true)
            cleared = (await router.RouteAsync<IPlatformNativeLoginCapability, PlatformUnit>(providerId,
                (capability, token) => capability.ClearLoginDataAsync(ownerHandle, token))).IsSuccess;
        await SendPlatformConfigurationAsync();
        await ShowPlatformLoginErrorAsync(cleared ? "已断开平台账号，本地音乐与已收藏的歌曲不会删除。"
            : "账号已断开，但登录网页数据未能清除。请检查插件后重试。");
    }

    private Task ShowPlatformLoginErrorAsync(string message) =>
        ExecuteScriptAsync($"window.Auralis?.showToast({JsonSerializer.Serialize(message)})");

    private static void ClosePlatformLoginSession(IPlatformNativeLoginSession session)
    {
        try { session.Close(); } catch { /* Optional plugin code must not block revocation or shutdown. */ }
    }

    private static void CancelPlatformLogin(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); } catch { /* A plugin cancellation callback may throw. */ }
    }

    private void ClosePlatformLogins()
    {
        _platformLoginGenerations.Clear();
        foreach (var pending in _openingPlatformLogins.Values.ToArray()) CancelPlatformLogin(pending);
        _openingPlatformLogins.Clear();
        foreach (var session in _platformLogins.Values.ToArray())
        {
            ClosePlatformLoginSession(session);
        }
        _platformLogins.Clear();
    }
}
