using System.Runtime.InteropServices;

namespace NvUv.Dcc.Core;

public sealed record DccGameDisplayInfo(
    int GameClientWidth,
    int GameClientHeight,
    int DisplayWidth,
    int DisplayHeight,
    int DisplayRefreshHz = 0,
    bool IsPrimary = false,
    string DisplayDeviceName = "")
{
    public static DccGameDisplayInfo Unknown { get; } = new(0, 0, 0, 0, 0);

    public string GameClientLogValue => Resolution(GameClientWidth, GameClientHeight);
    public string DisplayLogValue => Resolution(DisplayWidth, DisplayHeight);
    public string DisplayRefreshLogValue => DisplayRefreshHz > 1
        ? DisplayRefreshHz.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "unknown";

    private static string Resolution(int width, int height) =>
        width > 0 && height > 0 ? $"{width}x{height}" : "unknown";
}

public static class DccGameDisplayProbe
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorInfoFlagPrimary = 1;

    public static bool CanResolveNvidiaEfficiencyTarget(
        DccGameDisplayInfo primaryDisplay,
        bool manualTarget) =>
        manualTarget ||
        (primaryDisplay.IsPrimary &&
         primaryDisplay.DisplayRefreshHz > 1 &&
         !string.IsNullOrWhiteSpace(primaryDisplay.DisplayDeviceName));

    public static DccGameDisplayInfo Capture(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsWindows())
            return DccGameDisplayInfo.Unknown;

        var candidates = new List<WindowCandidate>();
        try
        {
            EnumWindows((window, parameter) =>
            {
                if (!IsWindowVisible(window))
                    return true;
                _ = GetWindowThreadProcessId(window, out uint ownerProcessId);
                if (ownerProcessId != (uint)processId || !GetClientRect(window, out Rect client))
                    return true;

                int width = Math.Max(0, client.Right - client.Left);
                int height = Math.Max(0, client.Bottom - client.Top);
                if (width > 0 && height > 0)
                    candidates.Add(new WindowCandidate(window, width, height));
                return true;
            }, IntPtr.Zero);

            WindowCandidate selected = SelectLargestClient(candidates);
            if (selected.Window == IntPtr.Zero)
                return DccGameDisplayInfo.Unknown;

            IntPtr monitor = MonitorFromWindow(selected.Window, MonitorDefaultToNearest);
            return CaptureMonitor(monitor, selected.Width, selected.Height);
        }
        catch
        {
            return DccGameDisplayInfo.Unknown;
        }
    }

    public static DccGameDisplayInfo CapturePrimary()
    {
        if (!OperatingSystem.IsWindows())
            return DccGameDisplayInfo.Unknown;

        try
        {
            IntPtr monitor = MonitorFromPoint(new Point(0, 0), MonitorDefaultToPrimary);
            return CaptureMonitor(monitor, 0, 0);
        }
        catch
        {
            return DccGameDisplayInfo.Unknown;
        }
    }

    private static DccGameDisplayInfo CaptureMonitor(
        IntPtr monitor,
        int gameClientWidth,
        int gameClientHeight)
    {
        var monitorInfo = new MonitorInfoEx
        {
            Size = Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty
        };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
            return new DccGameDisplayInfo(gameClientWidth, gameClientHeight, 0, 0);

        int displayWidth = Math.Max(0, monitorInfo.Monitor.Right - monitorInfo.Monitor.Left);
        int displayHeight = Math.Max(0, monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top);
        string deviceName = monitorInfo.DeviceName ?? string.Empty;
        return new DccGameDisplayInfo(
            gameClientWidth,
            gameClientHeight,
            displayWidth,
            displayHeight,
            ReadDisplayRefreshHz(deviceName),
            (monitorInfo.Flags & MonitorInfoFlagPrimary) != 0,
            deviceName);
    }

    internal static WindowCandidate SelectLargestClient(IEnumerable<WindowCandidate> candidates) =>
        candidates
            .Where(candidate => candidate.Width > 0 && candidate.Height > 0)
            .OrderByDescending(candidate => (long)candidate.Width * candidate.Height)
            .FirstOrDefault();

    internal readonly record struct WindowCandidate(IntPtr Window, int Width, int Height);

    private static int ReadDisplayRefreshHz(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return 0;

        IntPtr displayDc = CreateDC(null, deviceName, null, IntPtr.Zero);
        if (displayDc == IntPtr.Zero)
            return 0;

        try
        {
            const int verticalRefresh = 116;
            int refreshHz = GetDeviceCaps(displayDc, verticalRefresh);
            return refreshHz > 1 ? refreshHz : 0;
        }
        finally
        {
            _ = DeleteDC(displayDc);
        }
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point
    {
        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }

        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(
        string? driver,
        string? device,
        string? output,
        IntPtr initializationData);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr deviceContext, int index);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);
}
