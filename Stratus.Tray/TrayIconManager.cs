using System.Runtime.InteropServices;

namespace Stratus.Tray;

/// <summary>
/// Notification-area presence for the Stratus app, built directly on Shell_NotifyIcon so the
/// process never loads Windows Forms. It lives in its own library because the tray is not XAML
/// and the app project stays WinUI only. The owner supplies UI-thread callbacks; all events are
/// raised on the creating thread, which is where the hidden message window receives its messages.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    // App-defined messages must sit in the WM_APP range so they never collide with shell messages.
    private const uint WmTrayCallback = 0x8000 + 1;
    private const int WmLbuttonUp = 0x0202;
    private const int WmRbuttonUp = 0x0205;
    private const IntPtr CmdOpen = (IntPtr)1;
    private const IntPtr CmdExit = (IntPtr)2;

    private const int NimAdd = 0x0;
    private const int NimModify = 0x1;
    private const int NimDelete = 0x2;

    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifInfo = 0x10;
    private const uint NiifInfo = 0x1;

    private const uint MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2;
    private const uint TpmNonotify = 0x80;
    private const uint TpmReturncmd = 0x100;

    private static readonly Dictionary<IntPtr, TrayIconManager> Instances = new();
    private static readonly WndProcDelegate WndProcCallback = WndProc;
    private static bool _windowClassRegistered;

    private readonly Action _showWindow;
    private readonly Action _requestExit;
    private readonly Action<string>? _log;
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private readonly IntPtr _iconHandle;
    private readonly bool _ownIcon;
    private string _tooltip = "Stratus";
    private bool _balloonShown;
    private bool _disposed;

    public TrayIconManager(Action showWindow, Action requestExit, Action<string>? log = null)
    {
        _showWindow = showWindow;
        _requestExit = requestExit;
        _log = log;

        _iconHandle = CreateScoreIcon(out _ownIcon);
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _hwnd = CreateMessageWindow();
        if (_hwnd == IntPtr.Zero)
        {
            _log?.Invoke("Tray icon unavailable: the tray message window could not be created.");
            return;
        }

        AddOrUpdate(NimAdd, TrayFlags());
    }

    /// <summary>Tray tooltip doubles as a score glance: "Stratus: 84/100".</summary>
    public void UpdateTooltip(int score) => SetTooltip($"Stratus: {score}/100");

    /// <summary>Shown when no honest score exists: the tray never keeps a stale number.</summary>
    public void UpdateTooltipUnknown() => SetTooltip("Stratus: score unavailable");

    /// <summary>One-time hint so hiding to the tray never looks like the app vanished.</summary>
    public void OnHiddenToTray()
    {
        if (_balloonShown || _hwnd == IntPtr.Zero)
            return;
        _balloonShown = true;
        var data = BuildData(TrayFlags() | NifInfo);
        data.szInfo = "It stays in the notification area and keeps tracking your score. Click the icon to reopen.";
        data.szInfoTitle = "Stratus keeps running";
        data.dwInfoFlags = (int)NiifInfo;
        Shell_NotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            Instances.Remove(_hwnd);
            var data = BuildData(0);
            Shell_NotifyIconW(NimDelete, ref data);
            DestroyWindow(_hwnd);
        }

        if (_ownIcon && _iconHandle != IntPtr.Zero)
            DestroyIcon(_iconHandle);
    }

    private uint TrayFlags() => NifMessage | NifTip | (_iconHandle != IntPtr.Zero ? NifIcon : 0);

    private void SetTooltip(string text)
    {
        if (_hwnd == IntPtr.Zero || _tooltip == text)
            return;
        _tooltip = text;
        AddOrUpdate(NimModify, TrayFlags());
    }

    private void AddOrUpdate(int message, uint flags)
    {
        var data = BuildData(flags);
        Shell_NotifyIconW(message, ref data);
    }

    private NOTIFYICONDATAW BuildData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WmTrayCallback,
        hIcon = _iconHandle,
        szTip = _tooltip,
    };

    /// <summary>Explorer restarted and the taskbar with it, so the icon must be added again.</summary>
    private void ReAdd()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
            return;
        var removed = BuildData(0);
        Shell_NotifyIconW(NimDelete, ref removed);
        AddOrUpdate(NimAdd, TrayFlags());
    }

    private void ShowMenu()
    {
        if (!GetCursorPos(out var point))
            return;

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenuW(menu, 0, CmdOpen, "Open Stratus");
            AppendMenuW(menu, MfSeparator, IntPtr.Zero, null);
            AppendMenuW(menu, 0, CmdExit, "Exit");

            // TrackPopupMenu dismisses on outside clicks only while its window is foreground;
            // the WM_NULL afterwards releases the focus the call took.
            SetForegroundWindow(_hwnd);
            var command = (IntPtr)TrackPopupMenuEx(menu, TpmReturncmd | TpmRightButton | TpmNonotify, point.X, point.Y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);

            if (command == CmdOpen)
                _showWindow();
            else if (command == CmdExit)
            {
                _log?.Invoke("Exit requested from the tray menu.");
                _requestExit();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private IntPtr CreateMessageWindow()
    {
        try
        {
            if (!_windowClassRegistered)
            {
                var wndClass = new WNDCLASSW
                {
                    lpfnWndProc = WndProcCallback,
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = "StratusTrayWindow",
                };
                if (RegisterClassW(ref wndClass) == 0)
                    return IntPtr.Zero;
                _windowClassRegistered = true;
            }

            // A hidden top-level window, never shown: TrackPopupMenu can take foreground from it,
            // and WS_EX_TOOLWINDOW keeps it out of any window enumeration.
            var hwnd = CreateWindowExW(
                0x80 /* WS_EX_TOOLWINDOW */, "StratusTrayWindow", "Stratus tray", 0x80000000 /* WS_POPUP */,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (hwnd != IntPtr.Zero)
                Instances[hwnd] = this;
            return hwnd;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmTrayCallback && Instances.TryGetValue(hwnd, out var owner))
        {
            var mouse = unchecked((int)(long)lParam);
            if (mouse == WmLbuttonUp)
                owner._showWindow();
            else if (mouse == WmRbuttonUp)
                owner.ShowMenu();
            return IntPtr.Zero;
        }

        if (message != 0 && Instances.TryGetValue(hwnd, out var instance) && message == instance._taskbarCreatedMessage)
        {
            instance.ReAdd();
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// Draws the brand mark (blue rounded square, white S) into a 32bpp DIB and turns it into an
    /// alpha icon, matching the look of the previous GDI+ icon. Falls back to the shared
    /// application icon when any GDI step fails; the tray then shows a generic icon.
    /// </summary>
    private static IntPtr CreateScoreIcon(out bool owned)
    {
        owned = false;
        try
        {
            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                return IntPtr.Zero;
            try
            {
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = 32;
                bmi.bmiHeader.biHeight = -32; // top-down rows
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;

                var bitmap = CreateDIBSection(screenDc, ref bmi, 0 /* DIB_RGB_COLORS */, out var bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                    return IntPtr.Zero;

                var memoryDc = CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero)
                {
                    DeleteObject(bitmap);
                    return IntPtr.Zero;
                }

                try
                {
                    var oldBitmap = SelectObject(memoryDc, bitmap);
                    var fill = ColorRef(0x0F, 0x6C, 0xBD);
                    var brush = CreateSolidBrush(fill);
                    var pen = CreatePen(0 /* PS_SOLID */, 1, fill);
                    var oldBrush = SelectObject(memoryDc, brush);
                    var oldPen = SelectObject(memoryDc, pen);

                    // A 16x16 ellipse gives the same 8px corner radius the GDI+ icon drew.
                    RoundRect(memoryDc, 0, 0, 32, 32, 16, 16);

                    var font = CreateFontW(-15, 0, 0, 0, 700 /* FW_BOLD */, 0, 0, 0, 0, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, "Segoe UI");
                    var oldFont = SelectObject(memoryDc, font);
                    var textRect = new RECT(0, 0, 32, 32);
                    DrawTextW(memoryDc, "S", 1, ref textRect, 0x25 /* DT_SINGLELINE | DT_VCENTER | DT_CENTER */);

                    SelectObject(memoryDc, oldFont);
                    SelectObject(memoryDc, oldBrush);
                    SelectObject(memoryDc, oldPen);
                    SelectObject(memoryDc, oldBitmap);
                    DeleteObject(font);
                    DeleteObject(brush);
                    DeleteObject(pen);

                    // GDI writes straight colors but leaves the alpha channel at zero. Every drawn
                    // pixel becomes opaque and the untouched corners stay transparent.
                    for (var i = 0; i < 32 * 32; i++)
                    {
                        var pixel = Marshal.ReadInt32(bits, i * 4);
                        if (pixel != 0)
                            Marshal.WriteInt32(bits, i * 4, (int)((uint)pixel | 0xFF000000));
                    }

                    var info = new ICONINFO
                    {
                        fIcon = true,
                        hbmMask = CreateBitmap(32, 32, 1, 1, IntPtr.Zero),
                        hbmColor = bitmap,
                    };
                    var icon = CreateIconIndirect(ref info);
                    DeleteObject(info.hbmMask);
                    DeleteObject(bitmap);
                    if (icon != IntPtr.Zero)
                        owned = true;
                    return icon;
                }
                finally
                {
                    DeleteDC(memoryDc);
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static uint ColorRef(byte r, byte g, byte b) => (uint)(b << 16 | g << 8 | r);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public NOTIFYICONDATAW()
        {
            szTip = "";
            szInfo = "";
            szInfoTitle = "";
        }

        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip = "";
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo = "";
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle = "";
        public int dwInfoFlags;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePen(int style, int width, uint color);

    [DllImport("gdi32.dll")]
    private static extern bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr dc, string text, int length, ref RECT rect, uint format);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int width, int height, int planes, int bitsPerPixel, IntPtr bits);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
