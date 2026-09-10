using System.Runtime.InteropServices;

namespace Auralis.Services;

internal static class ClipboardWriteRetry
{
    // Short, bounded retries; release the UI thread while another process owns the clipboard.
    internal static async Task<bool> TryAsync(Func<Task> write, Func<int, Task>? delay = null)
    {
        delay ??= milliseconds => Task.Delay(milliseconds);
        var delays = new[] { 60, 120, 240, 360 };
        for (var attempt = 0; ; attempt++)
        {
            try { await write(); return true; }
            catch (ExternalException)
            {
                if (attempt == delays.Length) return false;
                await delay(delays[attempt]);
            }
        }
    }
}
