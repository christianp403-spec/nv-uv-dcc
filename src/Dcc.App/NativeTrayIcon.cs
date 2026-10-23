using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NvUv.Dcc.App;

internal enum NativeTrayState
{
    Off,
    Active,
    Recovery
}

/// <summary>
/// Small Shell_NotifyIcon wrapper used instead of WinForms NotifyIcon. Keeping
/// the tray native makes the WPF executable compatible with the .NET trimmer.
/// </summary>
internal sealed class NativeTrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = 0x8001;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint NotifyIconVersion4 = 4;
    private const int WmContextMenu = 0x007B;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmNonotify = 0x0080;
    private const uint OpenCommand = 1;
    private const uint ExitCommand = 2;
    private static readonly IntPtr MessageOnlyWindow = new(-3);

    private readonly Action _show;
    private readonly Action _exit;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreatedMessage;
    private readonly Dictionary<NativeTrayState, IntPtr> _icons;
    private NativeTrayState _state;
    private string _toolTip = "DCC · Dynamic Clock Capping";
    private string _openText = "Open DCC";
    private string _exitText = "Exit";
    private bool _added;
    private bool _disposed;

    public NativeTrayIcon(Action show, Action exit)
    {
        _show = show;
        _exit = exit;
        _icons = new Dictionary<NativeTrayState, IntPtr>
        {
            [NativeTrayState.Off] = LoadIcon("/Assets/tray_off.ico"),
            [NativeTrayState.Active] = LoadIcon("/Assets/tray_active.ico"),
            [NativeTrayState.Recovery] = LoadIcon("/Assets/tray_recovery.ico")
        };

        var parameters = new HwndSourceParameters("NV-UV.DCC.Tray")
        {
            ParentWindow = MessageOnlyWindow,
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WindowProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        AddIcon();
    }

    public void Update(NativeTrayState state, string toolTip, string openText, string exitText)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _state = state;
        _toolTip = Truncate(toolTip, 127);
        _openText = string.IsNullOrWhiteSpace(openText) ? "Open DCC" : openText;
        _exitText = string.IsNullOrWhiteSpace(exitText) ? "Exit" : exitText;
        if (!_added)
            AddIcon();
        else
            ShellNotifyIcon(NimModify, CreateData(NifIcon | NifTip | NifShowTip));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_added)
            ShellNotifyIcon(NimDelete, CreateData(0));
        _source.RemoveHook(WindowProc);
        _source.Dispose();
        foreach (IntPtr icon in _icons.Values)
        {
            if (icon != IntPtr.Zero)
                DestroyIcon(icon);
        }
        _icons.Clear();
        _added = false;
    }

    private void AddIcon()
    {
        if (_disposed)
            return;
        _added = ShellNotifyIcon(
            NimAdd,
            CreateData(NifMessage | NifIcon | NifTip | NifShowTip));
        if (!_added)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not add the DCC tray icon.");

        NotifyIconData version = CreateData(0);
        version.TimeoutOrVersion = NotifyIconVersion4;
        ShellNotifyIcon(NimSetVersion, version);
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _source.Handle,
        Id = IconId,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _icons.GetValueOrDefault(_state),
        Tip = Truncate(_toolTip, 127)
    };

    private IntPtr WindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreatedMessage)
        {
            _added = false;
            AddIcon();
            handled = true;
            return IntPtr.Zero;
        }
        if (message != CallbackMessage)
            return IntPtr.Zero;

        int notification = unchecked((ushort)lParam.ToInt64());
        switch (notification)
        {
            case WmLButtonDblClk:
                _show();
                handled = true;
                break;
            case WmContextMenu:
            case WmRButtonUp:
                ShowMenu(window);
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu(IntPtr owner)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;
        try
        {
            AppendMenu(menu, MfString, OpenCommand, _openText);
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, ExitCommand, _exitText);
            GetCursorPos(out Point cursor);
            SetForegroundWindow(owner);
            uint selected = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmReturnCmd | TpmNonotify,
                cursor.X,
                cursor.Y,
                owner,
                IntPtr.Zero);
            if (selected == OpenCommand)
                _show();
            else if (selected == ExitCommand)
                _exit();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static IntPtr LoadIcon(string resourcePath)
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri(resourcePath, UriKind.Relative));
        if (resource?.Stream is null)
            throw new FileNotFoundException($"Tray icon resource not found: {resourcePath}");

        BitmapFrame source;
        using (resource.Stream)
        {
            BitmapDecoder decoder = BitmapDecoder.Create(
                resource.Stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            source = decoder.Frames
                .OrderBy(frame => Math.Abs(frame.PixelWidth - 32) + Math.Abs(frame.PixelHeight - 32))
                .First();
        }

        BitmapSource bitmap = source;
        if (bitmap.PixelWidth != 32 || bitmap.PixelHeight != 32)
        {
            bitmap = new TransformedBitmap(
                bitmap,
                new ScaleTransform(32d / bitmap.PixelWidth, 32d / bitmap.PixelHeight));
        }
        if (bitmap.Format != PixelFormats.Bgra32)
            bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

        const int width = 32;
        const int height = 32;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = (uint)pixels.Length
            }
        };
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr color = CreateDIBSection(screen, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, screen);
        if (color == IntPtr.Zero || bits == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create tray icon bitmap.");
        Marshal.Copy(pixels, 0, bits, pixels.Length);

        int maskStride = ((width + 15) / 16) * 2;
        IntPtr mask = CreateBitmap(width, height, 1, 1, new byte[maskStride * height]);
        try
        {
            var iconInfo = new IconInfo { IsIcon = true, ColorBitmap = color, MaskBitmap = mask };
            IntPtr icon = CreateIconIndirect(ref iconInfo);
            if (icon == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create tray icon handle.");
            return icon;
        }
        finally
        {
            if (mask != IntPtr.Zero)
                DeleteObject(mask);
            DeleteObject(color);
        }
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 1)] + "…";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
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
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    private static bool ShellNotifyIcon(uint message, NotifyIconData data) => ShellNotifyIcon(message, ref data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref IconInfo iconInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr deviceContext, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
}
