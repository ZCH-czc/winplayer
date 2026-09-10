using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Auralis.Services;

/// <summary>One host per Windows user/session, independent of package version or executable directory.
/// Acquire and dispose on the same thread (the WPF application dispatcher owns the mutex).</summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    internal sealed record Activation(string? AudioFile, string Build);
    private readonly Mutex _mutex;
    private readonly Mutex _participant;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    public bool IsPrimary { get; }

    public SingleInstanceCoordinator(string? testIdentity = null)
    {
        var identity = testIdentity ?? $"Auralis.Player.v1.{WindowsIdentity.GetCurrent().User?.Value}.{Process.GetCurrentProcess().SessionId}";
        _pipeName = identity;
        _participant = new Mutex(false, $"Local\\{identity}.participant.{Environment.ProcessId}");
        _mutex = new Mutex(false, $"Local\\{identity}");
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }

    public void Listen(Action<Activation> activate)
    {
        if (!IsPrimary || _listener is not null) throw new InvalidOperationException();
        _listener = ListenAsync(activate);
    }

    public bool IsParticipatingProcess(int processId)
    {
        try
        {
            if (!Mutex.TryOpenExisting($"Local\\{_pipeName}.participant.{processId}", out var participant)) return false;
            participant.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task ListenAsync(Action<Activation> activate)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var size = new byte[4];
                await pipe.ReadExactlyAsync(size, timeout.Token).ConfigureAwait(false);
                var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(size);
                if (length is < 1 or > 16384) continue;
                var bytes = new byte[length];
                await pipe.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<Activation>(bytes);
                if (request is null || request.Build is null || request.Build.Length > 128 ||
                    request.AudioFile?.Length > 4096) continue;
                // The callback only queues work on the dispatcher. Never wait for UI startup here.
                activate(request);
                await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
            {
                if (!_shutdown.IsCancellationRequested)
                    try { await Task.Delay(100, _shutdown.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
            }
        }
    }

    public async Task<bool> ForwardAsync(Activation activation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(activation);
        if (bytes.Length > 16384) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcess))
                _ = AllowSetForegroundWindow(serverProcess);
            var size = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
            await pipe.WriteAsync(size, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            var reply = new byte[1];
            await pipe.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
            return reply[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        // Keep ownership until listener completion; otherwise an exiting host could acknowledge a new launch.
        try { _listener?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _participant.Dispose();
        _shutdown.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
