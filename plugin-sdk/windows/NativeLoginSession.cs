using System.Windows;
using System.Windows.Interop;
using Auralis.Platform.Abstractions;

namespace Auralis;

// Linked into trusted desktop plugins. No reference to the player's assembly or main WebView.
internal sealed class NativeLoginSession(
    Func<Window, bool, Action, CancellationToken, Window> createWindow) : IPlatformNativeLoginSession
{
    private readonly CancellationTokenSource _lifetime = new();
    private Window? _window;
    private bool _closed;
    public event EventHandler? Authenticated;
    public event EventHandler? Closed;

    internal static Window Owner(nint handle) => HwndSource.FromHwnd(handle)?.RootVisual as Window
        ?? throw new InvalidOperationException("登录窗口的宿主已关闭。");

    public void Show(nint ownerWindow, bool dark)
    {
        if (_closed) return;
        if (_window is not null) { Activate(); return; }
        _window = createWindow(Owner(ownerWindow), dark, () =>
        {
            if (!_closed && !_lifetime.IsCancellationRequested) Authenticated?.Invoke(this, EventArgs.Empty);
        }, _lifetime.Token);
        _window.Closed += (_, _) => Finish();
        _window.Show();
    }

    public void Activate() { if (!_closed) _window?.Activate(); }
    public void Close()
    {
        if (_closed) return;
        _lifetime.Cancel();
        _window?.Close();
        Finish();
    }
    private void Finish()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
        Authenticated = null;
        Closed = null;
        _window = null;
    }

    internal static async Task<PlatformResult<PlatformUnit>> ClearAsync(
        nint owner, Func<Window, Task> clear, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pending = await Application.Current.Dispatcher.InvokeAsync(() => clear(Owner(owner)));
        await pending;
        return PlatformResult<PlatformUnit>.Success(PlatformUnit.Value);
    }
}
