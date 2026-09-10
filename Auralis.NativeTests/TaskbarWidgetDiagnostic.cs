using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class TaskbarWidgetDiagnostic
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = unchecked((long)0x80000000);
    private const long WsVisible = 0x10000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint PwRenderFullContent = 0x00000002;
    private const uint Srccopy = 0x00CC0020;
    private const uint DibRgbColors = 0;

    public static int Run(IReadOnlyList<string> args)
    {
        if (!TryParseOptions(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var candidateProcessIds = options.ProcessId is { } processId
            ? new HashSet<uint> { checked((uint)processId) }
            : Process.GetProcessesByName("Auralis")
                .Select(process => checked((uint)process.Id))
                .ToHashSet();

        var taskbarHandles = EnumerateTopLevelWindows()
            .Where(handle => GetClassName(handle) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            .ToList();
        var primaryTaskbar = FindWindowW("Shell_TrayWnd", null);
        if (primaryTaskbar != IntPtr.Zero)
        {
            taskbarHandles.Add(primaryTaskbar);
        }

        var taskbars = taskbarHandles
            .Distinct()
            .ToArray();

        var deadline = DateTime.UtcNow.AddMilliseconds(options.WaitVisibleMilliseconds);
        WindowSnapshot[] candidates;
        do
        {
            candidates = FindCandidates(taskbars, candidateProcessIds, options.IncludeFlyout);
            if (candidates.Any(candidate => candidate.Visible))
            {
                if (options.SettleMilliseconds > 0)
                {
                    Thread.Sleep(options.SettleMilliseconds);
                    candidates = FindCandidates(taskbars, candidateProcessIds, options.IncludeFlyout);
                }
                break;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            Thread.Sleep(50);
        }
        while (true);

        var requestedHandle = options.WindowHandle;
        var selected = requestedHandle is { } exactHandle
            ? candidates.FirstOrDefault(candidate => candidate.HandleValue == exactHandle.ToInt64())
            : candidates.FirstOrDefault(candidate => candidate.Visible && !candidate.DirectTaskbarChild)
                ?? candidates.FirstOrDefault();
        var violations = ValidateCandidates(candidates, options.IncludeFlyout);

        ScreenshotResult? screenshot = null;
        if (!string.IsNullOrWhiteSpace(options.ScreenshotPath))
        {
            if (selected is null)
            {
                Console.Error.WriteLine("No matching Auralis taskbar widget window was available to capture.");
                PrintReport(taskbars, candidateProcessIds, candidates, selected, screenshot, violations);
                return 3;
            }

            screenshot = CaptureWindow(new IntPtr(selected.HandleValue), options.ScreenshotPath!, options.CaptureScreenPixels);
        }

        PrintReport(taskbars, candidateProcessIds, candidates, selected, screenshot, violations);
        if (candidates.Length == 0)
        {
            return 3;
        }

        if (violations.Count > 0)
        {
            foreach (var violation in violations)
            {
                Console.Error.WriteLine(violation);
            }
            return 4;
        }

        return 0;
    }

    private static void PrintReport(
        IReadOnlyList<IntPtr> taskbars,
        IReadOnlySet<uint> processIds,
        IReadOnlyList<WindowSnapshot> candidates,
        WindowSnapshot? selected,
        ScreenshotResult? screenshot,
        IReadOnlyList<string> violations)
    {
        var report = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            processIds = processIds.Order().ToArray(),
            taskbars = taskbars.Select(handle => new
            {
                hwnd = FormatHandle(handle),
                className = GetClassName(handle),
                rect = GetRect(handle),
                dpi = GetDpiForWindow(handle)
            }),
            candidateCount = candidates.Count,
            visibleCandidateCount = candidates.Count(candidate => candidate.Visible),
            candidates,
            selectedHwnd = selected?.Handle,
            screenshot,
            violations
        };

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    private static WindowSnapshot[] FindCandidates(
        IReadOnlyList<IntPtr> taskbars,
        IReadOnlySet<uint> processIds,
        bool includeFlyout)
    {
        var snapshots = includeFlyout
            ? EnumerateTopLevelWindows()
                .Where(handle => string.Equals(GetWindowText(handle), "Auralis 音乐弹窗", StringComparison.Ordinal))
                .Select(handle => CreateSnapshot(IntPtr.Zero, handle))
            : EnumerateTopLevelWindows()
                .Where(handle => string.Equals(GetWindowText(handle), "Auralis 任务栏音乐组件", StringComparison.Ordinal))
                .Select(handle => CreateSnapshot(FindOverlappingTaskbar(taskbars, handle), handle))
                .Concat(taskbars.SelectMany(taskbar => EnumerateChildWindows(taskbar)
                    .Where(handle => string.Equals(GetWindowText(handle), "Auralis 任务栏音乐组件", StringComparison.Ordinal))
                    .Select(handle => CreateSnapshot(taskbar, handle))));

        return snapshots
            .Where(snapshot => processIds.Contains(snapshot.ProcessId))
            .GroupBy(snapshot => snapshot.HandleValue)
            .Select(group => group.First())
            .OrderByDescending(snapshot => snapshot.Visible)
            .ThenBy(snapshot => snapshot.DirectTaskbarChild)
            .ThenByDescending(snapshot => snapshot.Rect.Width * snapshot.Rect.Height)
            .ToArray();
    }

    private static IReadOnlyList<string> ValidateCandidates(
        IReadOnlyList<WindowSnapshot> candidates,
        bool includeFlyout)
    {
        if (includeFlyout)
        {
            return [];
        }

        var violations = new List<string>();
        var visible = candidates.Where(candidate => candidate.Visible).ToArray();
        if (visible.Length > 1)
        {
            violations.Add($"Unsafe state: {visible.Length} Auralis taskbar widgets are visible; the cross-process lease must allow only one.");
        }

        foreach (var candidate in candidates)
        {
            if (candidate.DirectTaskbarChild || candidate.StyleFlags.Child ||
                candidate.ParentClassName is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                violations.Add($"Unsafe state: widget {candidate.Handle} is owned or parented by Explorer instead of remaining an Auralis top-level tool window.");
            }

            if (candidate.Visible && (!candidate.StyleFlags.ToolWindow || !candidate.StyleFlags.NoActivate))
            {
                violations.Add($"Unsafe state: visible widget {candidate.Handle} is missing WS_EX_TOOLWINDOW or WS_EX_NOACTIVATE.");
            }
        }

        return violations;
    }

    private static WindowSnapshot CreateSnapshot(IntPtr taskbar, IntPtr handle)
    {
        _ = GetWindowThreadProcessId(handle, out var processId);
        var parent = GetParent(handle);
        var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        var exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        var rect = GetRect(handle);
        var clientRect = GetClientRectValue(handle);

        return new WindowSnapshot(
            HandleValue: handle.ToInt64(),
            Handle: FormatHandle(handle),
            TaskbarHandle: FormatHandle(taskbar),
            ParentHandle: FormatHandle(parent),
            ParentClassName: GetClassName(parent),
            DirectTaskbarChild: taskbar != IntPtr.Zero && parent == taskbar,
            AlignedToTaskbar: taskbar != IntPtr.Zero && IntersectionArea(rect, GetRect(taskbar)) > 0,
            ProcessId: processId,
            ClassName: GetClassName(handle),
            Title: GetWindowText(handle),
            Visible: IsWindowVisible(handle),
            Rect: rect,
            ClientRect: clientRect,
            Dpi: GetDpiForWindow(handle),
            Style: $"0x{unchecked((ulong)style):X16}",
            ExtendedStyle: $"0x{unchecked((ulong)exStyle):X16}",
            StyleFlags: new WindowStyleFlags(
                Child: (style & WsChild) != 0,
                Popup: (style & WsPopup) != 0,
                Visible: (style & WsVisible) != 0,
                ToolWindow: (exStyle & WsExToolWindow) != 0,
                NoActivate: (exStyle & WsExNoActivate) != 0));
    }

    private static IntPtr FindOverlappingTaskbar(IReadOnlyList<IntPtr> taskbars, IntPtr window)
    {
        var windowRect = GetRect(window);
        return taskbars
            .Select(taskbar => new { Handle = taskbar, Area = IntersectionArea(windowRect, GetRect(taskbar)) })
            .OrderByDescending(candidate => candidate.Area)
            .FirstOrDefault(candidate => candidate.Area > 0)?.Handle ?? IntPtr.Zero;
    }

    private static long IntersectionArea(RectValue left, RectValue right)
    {
        var width = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        var height = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
        return (long)width * height;
    }

    private static ScreenshotResult CaptureWindow(IntPtr handle, string path, bool captureScreenPixels)
    {
        var rect = GetRect(handle);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new InvalidOperationException("The selected window has an empty rectangle.");
        }

        var absolutePath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC failed.");
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previousObject = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC failed.");
            }

            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = checked((uint)Marshal.SizeOf<BitmapInfoHeader>()),
                    Width = rect.Width,
                    Height = -rect.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };

            bitmap = CreateDIBSection(screenDc, ref bitmapInfo, DibRgbColors, out _, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDIBSection failed.");
            }

            previousObject = SelectObject(memoryDc, bitmap);
            if (previousObject == IntPtr.Zero || previousObject == new IntPtr(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SelectObject failed.");
            }

            var captureMode = captureScreenPixels ? "ExactWindowRectangleBitBlt" : "PrintWindow";
            if (captureScreenPixels)
            {
                if (!BitBlt(memoryDc, 0, 0, rect.Width, rect.Height, screenDc, rect.Left, rect.Top, Srccopy))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Exact-rectangle BitBlt failed.");
                }
            }
            else if (!PrintWindow(handle, memoryDc, PwRenderFullContent))
            {
                if (!BitBlt(memoryDc, 0, 0, rect.Width, rect.Height, screenDc, rect.Left, rect.Top, Srccopy))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "PrintWindow and exact-rectangle BitBlt both failed.");
                }

                captureMode = "ExactWindowRectangleBitBlt";
            }

            var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmapSource.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            using var stream = File.Create(absolutePath);
            encoder.Save(stream);

            return new ScreenshotResult(
                Path: absolutePath,
                Hwnd: FormatHandle(handle),
                Width: rect.Width,
                Height: rect.Height,
                CaptureMode: captureMode);
        }
        finally
        {
            if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                _ = SelectObject(memoryDc, previousObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                _ = DeleteDC(memoryDc);
            }

            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        out DiagnosticOptions options,
        out string error)
    {
        int? processId = null;
        IntPtr? windowHandle = null;
        string? screenshotPath = null;
        var captureScreenPixels = false;
        var includeFlyout = false;
        var waitVisibleMilliseconds = 0;
        var settleMilliseconds = 0;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--pid" when index + 1 < args.Count && int.TryParse(args[++index], out var parsedProcessId) && parsedProcessId > 0:
                    processId = parsedProcessId;
                    break;

                case "--hwnd" when index + 1 < args.Count && TryParseHandle(args[++index], out var parsedHandle):
                    windowHandle = parsedHandle;
                    break;

                case "--screenshot" when index + 1 < args.Count:
                    screenshotPath = args[++index];
                    break;

                case "--screen-pixels":
                    captureScreenPixels = true;
                    break;

                case "--flyout":
                    includeFlyout = true;
                    break;

                case "--wait-visible-ms" when index + 1 < args.Count &&
                                               int.TryParse(args[++index], out var parsedWait) &&
                                               parsedWait is >= 0 and <= 30000:
                    waitVisibleMilliseconds = parsedWait;
                    break;

                case "--settle-ms" when index + 1 < args.Count &&
                                         int.TryParse(args[++index], out var parsedSettle) &&
                                         parsedSettle is >= 0 and <= 5000:
                    settleMilliseconds = parsedSettle;
                    break;

                default:
                    options = default;
                    error = $"Invalid taskbar-widget argument: {args[index]}";
                    return false;
            }
        }

        options = new DiagnosticOptions(
            processId,
            windowHandle,
            screenshotPath,
            captureScreenPixels,
            includeFlyout,
            waitVisibleMilliseconds,
            settleMilliseconds);
        error = string.Empty;
        return true;
    }

    private static bool TryParseHandle(string value, out IntPtr handle)
    {
        var numberStyle = System.Globalization.NumberStyles.Integer;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            numberStyle = System.Globalization.NumberStyles.HexNumber;
        }

        if (long.TryParse(value, numberStyle, System.Globalization.CultureInfo.InvariantCulture, out var raw) && raw > 0)
        {
            handle = new IntPtr(raw);
            return true;
        }

        handle = IntPtr.Zero;
        return false;
    }

    private static IEnumerable<IntPtr> EnumerateTopLevelWindows()
    {
        var windows = new List<IntPtr>();
        _ = EnumWindows((handle, _) =>
        {
            windows.Add(handle);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static IEnumerable<IntPtr> EnumerateChildWindows(IntPtr parent)
    {
        var windows = new List<IntPtr>();
        _ = EnumChildWindows(parent, (handle, _) =>
        {
            windows.Add(handle);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static RectValue GetRect(IntPtr handle)
    {
        return GetWindowRect(handle, out var rect)
            ? new RectValue(rect.Left, rect.Top, rect.Right, rect.Bottom, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : new RectValue(0, 0, 0, 0, 0, 0);
    }

    private static RectValue GetClientRectValue(IntPtr handle)
    {
        return GetClientRect(handle, out var rect)
            ? new RectValue(rect.Left, rect.Top, rect.Right, rect.Bottom, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : new RectValue(0, 0, 0, 0, 0, 0);
    }

    private static string GetClassName(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(256);
        return GetClassNameNative(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static string GetWindowText(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        var buffer = new StringBuilder(Math.Max(length + 1, 2));
        _ = GetWindowTextNative(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string FormatHandle(IntPtr handle) => $"0x{unchecked((ulong)handle.ToInt64()):X}";

    private readonly record struct DiagnosticOptions(
        int? ProcessId,
        IntPtr? WindowHandle,
        string? ScreenshotPath,
        bool CaptureScreenPixels,
        bool IncludeFlyout,
        int WaitVisibleMilliseconds,
        int SettleMilliseconds);

    private sealed record WindowSnapshot(
        long HandleValue,
        string Handle,
        string TaskbarHandle,
        string ParentHandle,
        string ParentClassName,
        bool DirectTaskbarChild,
        bool AlignedToTaskbar,
        uint ProcessId,
        string ClassName,
        string Title,
        bool Visible,
        RectValue Rect,
        RectValue ClientRect,
        uint Dpi,
        string Style,
        string ExtendedStyle,
        WindowStyleFlags StyleFlags);

    private sealed record WindowStyleFlags(
        bool Child,
        bool Popup,
        bool Visible,
        bool ToolWindow,
        bool NoActivate);

    private sealed record RectValue(int Left, int Top, int Right, int Bottom, int Width, int Height);

    private sealed record ScreenshotResult(string Path, string Hwnd, int Width, int Height, string CaptureMode);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    private delegate bool EnumWindowProc(IntPtr handle, IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr handle, StringBuilder className, int maximumCount);

    private static int GetClassNameNative(IntPtr handle, StringBuilder className, int maximumCount) =>
        GetClassNameW(handle, className, maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr handle, StringBuilder text, int maximumCount);

    private static int GetWindowTextNative(IntPtr handle, StringBuilder text, int maximumCount) =>
        GetWindowTextW(handle, text, maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr handle);

    private static int GetWindowTextLength(IntPtr handle) => GetWindowTextLengthW(handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLong32(IntPtr handle, int index);

    private static IntPtr GetWindowLongPtr(IntPtr handle, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : GetWindowLong32(handle, index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr handle, IntPtr deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);
}
