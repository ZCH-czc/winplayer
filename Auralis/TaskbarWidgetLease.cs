using System.IO;

namespace Auralis;

/// <summary>
/// Ensures that at most one Auralis process in the current Windows session can own the taskbar
/// widget. It is intentionally narrower than full single-instance activation: losing this lease
/// only disables the optional taskbar surface and never affects playback or the main window.
/// </summary>
internal sealed class TaskbarWidgetLease : IDisposable
{
    private const string DefaultMutexName = @"Local\Auralis.TaskbarMediaWidget.v1";
    private readonly Mutex? _mutex;
    private bool _ownsMutex;
    private bool _disposed;

    internal bool IsHeld => _ownsMutex;

    internal TaskbarWidgetLease(string? mutexName = null)
    {
        try
        {
            _mutex = new Mutex(false, mutexName ?? DefaultMutexName);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                           WaitHandleCannotBeOpenedException)
        {
            // Another process can pre-create a named kernel object with an incompatible type or ACL.
            // The taskbar surface is optional, so fail closed instead of taking down local playback.
            _mutex = null;
        }
    }

    internal bool TryAcquire()
    {
        if (_disposed || _mutex is null || _ownsMutex)
        {
            return _ownsMutex;
        }

        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    internal void Release()
    {
        var mutex = _mutex;
        if (!_ownsMutex || mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Ownership can only be lost during teardown; the optional widget must still dispose.
        }
        finally
        {
            _ownsMutex = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Release();
        _disposed = true;
        _mutex?.Dispose();
    }
}
