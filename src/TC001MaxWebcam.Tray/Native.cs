// TC001 Max Thermal Webcam tray: the Win32 calls it needs (user32, shell32, gdi32, kernel32).
//
// Origin: written for this project from Microsoft's Win32 documentation (Shell_NotifyIcon,
// TrackPopupMenuEx, CreateIconIndirect, CreateDIBSection, DwmSetWindowAttribute,
// GetDpiForMonitor, ChooseColor, RegisterApplicationRestart).

using System;
using System.Runtime.InteropServices;

namespace TC001MaxWebcam.Tray
{
    internal static class Native
    {
        // -- messages ------------------------------------------------------------------------------
        public const uint WM_DESTROY = 0x0002;
        public const uint WM_CLOSE = 0x0010;
        public const uint WM_QUERYENDSESSION = 0x0011;
        public const uint WM_ENDSESSION = 0x0016;
        public const uint WM_NULL = 0x0000;
        public const uint WM_TIMER = 0x0113;
        public const uint WM_CONTEXTMENU = 0x007B;
        public const uint WM_APP = 0x8000;
        public const uint NIN_SELECT = 0x0400;
        public const uint NIN_KEYSELECT = 0x0401;

        public const uint WM_ACTIVATE = 0x0006;
        public const uint WM_PAINT = 0x000F;
        public const uint WM_ERASEBKGND = 0x0014;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_MOUSEMOVE = 0x0200;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_MOUSELEAVE = 0x02A3;
        public const uint WM_DPICHANGED = 0x02E0;
        public const uint WM_SETTINGCHANGE = 0x001A;
        public const int VK_ESCAPE = 0x1B;

        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // -- windows, painting, monitors -----------------------------------------------------------
        public const uint WS_POPUP = 0x80000000;
        public const uint WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;
        public const int SW_HIDE = 0, SW_SHOW = 5;
        public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        public static readonly IntPtr HWND_TOPMOST = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public readonly int Width => Right - Left;
            public readonly int Height => Bottom - Top;
            public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
            public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public uint cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }
        public const uint TME_LEAVE = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        public struct NOTIFYICONIDENTIFIER
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

        [DllImport("user32.dll")]
        public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
        public static readonly IntPtr IDC_ARROW = new(32512);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINTV pt, uint flags);
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINTV { public int X, Y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("shell32.dll")]
        public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
        public const int DWMWCP_ROUND = 2;

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        public const uint SRCCOPY = 0x00CC0020;

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
            uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
            uint quality, uint pitchAndFamily, string face);
        public const int FW_NORMAL = 400, FW_SEMIBOLD = 600;
        public const uint CLEARTYPE_QUALITY = 5;

        [DllImport("gdi32.dll")]
        public static extern uint SetTextColor(IntPtr hdc, uint color);

        [DllImport("gdi32.dll")]
        public static extern int SetBkMode(IntPtr hdc, int mode);
        public const int TRANSPARENT = 1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);
        public const uint DT_LEFT = 0x0, DT_CENTER = 0x1, DT_RIGHT = 0x2, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20,
            DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;

        [DllImport("gdi32.dll")]
        public static extern bool GdiFlush();

        // -- the colour dialog ---------------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CHOOSECOLORW
        {
            public uint lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public uint rgbResult;
            public IntPtr lpCustColors;
            public uint Flags;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
        }
        public const uint CC_RGBINIT = 0x1, CC_FULLOPEN = 0x2, CC_ANYCOLOR = 0x100;

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
        public static extern bool ChooseColorW(ref CHOOSECOLORW cc);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
            public uint lPrivate;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessageW(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int code);

        [DllImport("user32.dll")]
        public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessageW(string name);

        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint ms, IntPtr proc);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
        private static extern bool GetCursorPosRaw(out POINT pt);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        public static (int X, int Y) CursorPos() => GetCursorPosRaw(out var p) ? (p.X, p.Y) : (0, 0);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);
        public const int SM_CXSMICON = 49;
        public const int SM_CYMENUCHECK = 72;

        [DllImport("user32.dll")]
        public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
        public const uint MB_ICONWARNING = 0x30;

        // -- menus ---------------------------------------------------------------------------------
        public const uint MF_STRING = 0x0000;
        public const uint MF_GRAYED = 0x0001;
        public const uint MF_CHECKED = 0x0008;
        public const uint MF_POPUP = 0x0010;
        public const uint MF_SEPARATOR = 0x0800;
        public const uint MF_BYCOMMAND = 0x0000;
        public const uint TPM_RIGHTBUTTON = 0x0002;
        public const uint TPM_RETURNCMD = 0x0100;
        public const uint TPM_NONOTIFY = 0x0080;
        public const uint MIIM_BITMAP = 0x0080;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MENUITEMINFOW
        {
            public uint cbSize;
            public uint fMask;
            public uint fType;
            public uint fState;
            public uint wID;
            public IntPtr hSubMenu;
            public IntPtr hbmpChecked;
            public IntPtr hbmpUnchecked;
            public IntPtr dwItemData;
            public IntPtr dwTypeData;
            public uint cch;
            public IntPtr hbmpItem;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr idOrSubmenu, string? text);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        public static extern bool CheckMenuRadioItem(IntPtr menu, uint first, uint last, uint check, uint flags);

        [DllImport("user32.dll")]
        public static extern bool SetMenuItemInfoW(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFOW info);

        [DllImport("user32.dll")]
        public static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

        [DllImport("user32.dll")]
        public static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpm);

        // -- notification area ---------------------------------------------------------------------
        public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_SHOWTIP = 0x80;
        public const uint NOTIFYICON_VERSION_4 = 4;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

        // -- bitmaps and icons ---------------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
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
        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bitsPerPixel, byte[] bits);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        public static extern IntPtr CreateIconIndirect(ref ICONINFO info);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        /// <summary>A 32-bit top-down DIB section holding <paramref name="bgra"/> (premultiplied).</summary>
        public static IntPtr Dib(int w, int h, byte[] bgra)
        {
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
            };
            var hbm = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            if (hbm != IntPtr.Zero && bits != IntPtr.Zero)
                Marshal.Copy(bgra, 0, bits, w * h * 4);
            return hbm;
        }

        /// <summary>An icon from premultiplied BGRA pixels (n x n). DestroyIcon it when done.</summary>
        public static IntPtr Icon(int n, byte[] bgra)
        {
            var color = Dib(n, n, bgra);
            var mask = CreateBitmap(n, n, 1, 1, new byte[(n + 15) / 16 * 2 * n]);
            var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
            var icon = CreateIconIndirect(ref info);
            DeleteObject(color);
            DeleteObject(mask);
            return icon;
        }

        // -- process -------------------------------------------------------------------------------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterApplicationRestart(string? commandLine, uint flags);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetModuleHandleW(IntPtr name);
    }
}
