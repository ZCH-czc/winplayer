using System.Runtime.InteropServices;

internal static class NativeWindowGuard
{
    // Observe only this silent test process; never close or modify another application's windows.
    internal static void AssertNoOwnedVisibleWindow()
    {
        var found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var process);
            if (process == Environment.ProcessId && IsWindowVisible(window)) found = true;
            return true;
        }, IntPtr.Zero);
        if (found) throw new InvalidOperationException("Decoder created a visible native output window.");
    }
    private delegate bool Callback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
