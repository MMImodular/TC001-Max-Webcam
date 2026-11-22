// TC001 Max Thermal Webcam tray: the settings flyout, a small window that opens above the tray
// icon (like Windows' own quick settings) and stays open while you change several settings. It
// closes when you click elsewhere or press Esc, or click the icon again.
//
// It has no child controls: one custom-painted window. Each paint lays the panel out from the top
// ("immediate mode"), draws anti-aliased shapes straight into a 32-bit bitmap, then draws the text
// with GDI (ClearType) and copies the bitmap to the window. The clickable areas recorded while
// drawing are what mouse clicks are tested against. Colours follow Windows' light / dark app
// setting and accent colour. Origin: written for this project.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using TC001MaxWebcam.Core;
using static TC001MaxWebcam.Tray.Native;

namespace TC001MaxWebcam.Tray
{
    internal sealed unsafe class Panel
    {
        private const string ClassName = "TC001MaxWebcamPanel";
        private const int WidthDip = 340;

        private readonly WndProc _proc;
        private readonly Func<WebcamSettings> _settings;
        private readonly Action<string[]> _set;       // "key=value" assignments, applied together
        private readonly Action _editFile, _openLog, _exit;
        private IntPtr _hwnd;
        private long _hiddenAt;
        private bool _picking;                         // the colour dialog is open
        private static readonly uint[] CustomColours = new uint[16]; // the dialog's 16 "custom colours"
        private static bool _coloursSeeded;

        // per paint
        private double _k = 1;                 // device pixels per DIP
        private Theme _t = Theme.Dark;
        private byte* _bits;
        private int _w, _h;
        private bool _draw;
        private readonly List<(RECT Rect, Action Click)> _hot = [];
        private readonly List<(string Text, RECT Rect, uint Color, IntPtr Font, uint Format)> _texts = [];
        private int _hover = -1;
        private IntPtr _font, _fontSmall, _fontTitle, _fontSection;
        private uint _fontDpi;

        public Panel(Func<WebcamSettings> settings, Action<string[]> set, Action editFile, Action openLog, Action exit)
        {
            _settings = settings;
            _set = set;
            _editFile = editFile;
            _openLog = openLog;
            _exit = exit;
            _proc = WindowProc;
        }

        public bool Visible => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);

        /// <summary>True for a short time after the panel closed: a click on the tray icon that closed
        /// it (by taking the focus) should not open it again at once.</summary>
        public bool JustHidden => Environment.TickCount64 - _hiddenAt < 400;

        public void Toggle(IntPtr owner, uint iconId)
        {
            if (Visible) { Hide(); return; }
            if (JustHidden) return;
            Show(owner, iconId);
        }

        public void Refresh()
        {
            if (Visible)
                InvalidateRect(_hwnd, IntPtr.Zero, false);
        }

        public void Hide()
        {
            if (!Visible)
                return;
            ShowWindow(_hwnd, SW_HIDE);
            _hiddenAt = Environment.TickCount64;
            _hover = -1;
        }

        private void Show(IntPtr owner, uint iconId)
        {
            Create();
            // where: above the tray icon, inside the work area of its monitor
            var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = owner, uID = iconId };
            POINTV anchor;
            if (Shell_NotifyIconGetRect(ref id, out var icon) == 0)
                anchor = new POINTV { X = (icon.Left + icon.Right) / 2, Y = (icon.Top + icon.Bottom) / 2 };
            else
            {
                var c = CursorPos();
                anchor = new POINTV { X = c.X, Y = c.Y };
            }
            var monitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(monitor, ref mi);
            var dpi = GetDpiForMonitor(monitor, 0, out var dx, out _) == 0 ? dx : 96u;
            _k = dpi / 96.0;
            _t = Theme.Current();

            var w = Px(WidthDip);
            var h = Measure(w);
            var work = mi.rcWork;
            var gap = Px(12);
            var x = anchor.X > (work.Left + work.Right) / 2 ? work.Right - w - gap : work.Left + gap;
            var y = anchor.Y > (work.Top + work.Bottom) / 2 ? work.Bottom - h - gap : work.Top + gap;
            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, 0);
            Decorate();
            ShowWindow(_hwnd, SW_SHOW);
            SetForegroundWindow(_hwnd);
            InvalidateRect(_hwnd, IntPtr.Zero, false);
        }

        private void Create()
        {
            if (_hwnd != IntPtr.Zero)
                return;
            var instance = GetModuleHandleW(IntPtr.Zero);
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = instance,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                lpszClassName = ClassName,
            };
            RegisterClassExW(ref wc);
            _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, ClassName, "TC001 Max Thermal Webcam settings", WS_POPUP,
                0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        }

        /// <summary>Windows 11 rounded corners, and a border in the theme's colour.</summary>
        private void Decorate()
        {
            var round = DWMWCP_ROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
            var dark = _t.IsDark ? 1 : 0;
            DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            var border = (int)Ref(_t.Border);
            DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref border, 4);
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_ACTIVATE:
                    if ((wParam.ToInt64() & 0xFFFF) == 0 && !_picking)
                        Hide(); // clicked elsewhere
                    return IntPtr.Zero;
                case WM_KEYDOWN:
                    if (wParam.ToInt64() == VK_ESCAPE)
                        Hide();
                    return IntPtr.Zero;
                case WM_ERASEBKGND:
                    return 1;
                case WM_PAINT:
                    Paint(hWnd);
                    return IntPtr.Zero;
                case WM_MOUSEMOVE:
                {
                    var (x, y) = Point(lParam);
                    var hover = _hot.FindIndex(h => h.Rect.Contains(x, y));
                    if (hover != _hover)
                    {
                        _hover = hover;
                        InvalidateRect(hWnd, IntPtr.Zero, false);
                    }
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = hWnd };
                    TrackMouseEvent(ref tme);
                    return IntPtr.Zero;
                }
                case WM_MOUSELEAVE:
                    if (_hover != -1)
                    {
                        _hover = -1;
                        InvalidateRect(hWnd, IntPtr.Zero, false);
                    }
                    return IntPtr.Zero;
                case WM_LBUTTONDOWN:
                {
                    var (x, y) = Point(lParam);
                    var hit = _hot.FirstOrDefault(h => h.Rect.Contains(x, y));
                    hit.Click?.Invoke();
                    if (Visible)
                        InvalidateRect(hWnd, IntPtr.Zero, false);
                    return IntPtr.Zero;
                }
                case WM_DPICHANGED:
                {
                    _k = (wParam.ToInt64() & 0xFFFF) / 96.0;
                    var r = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(lParam);
                    var w = Px(WidthDip);
                    SetWindowPos(hWnd, IntPtr.Zero, r.Left, r.Top, w, Measure(w), SWP_NOZORDER | SWP_NOACTIVATE);
                    InvalidateRect(hWnd, IntPtr.Zero, false);
                    return IntPtr.Zero;
                }
                case WM_SETTINGCHANGE:
                    _t = Theme.Current(); // light / dark or accent changed
                    if (_hwnd != IntPtr.Zero)
                    {
                        Decorate();
                        InvalidateRect(hWnd, IntPtr.Zero, false);
                    }
                    break;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private static (int X, int Y) Point(IntPtr lParam) =>
            ((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));

        // -- painting ------------------------------------------------------------------------------
        private int Measure(int width)
        {
            _draw = false;
            _w = width;
            var hover = _hover;
            var h = Layout();
            _hover = hover;
            return h;
        }

        private void Paint(IntPtr hWnd)
        {
            var ps = new PAINTSTRUCT();
            var hdc = BeginPaint(hWnd, out ps);
            try
            {
                GetClientRect(hWnd, out var client);
                if (client.Width <= 0 || client.Height <= 0)
                    return;
                Render(client.Width, client.Height, hdc, null);
            }
            finally
            {
                EndPaint(hWnd, ref ps);
            }
        }

        /// <summary>The panel as BGR pixels, without a window (for checking the look during
        /// development). <paramref name="scale"/> is the display scale (1 = 100 %).</summary>
        public byte[] Snapshot(double scale, bool dark, out int width, out int height)
        {
            _k = scale;
            _t = Theme.Make(dark, Theme.DefaultAccent);
            width = Px(WidthDip);
            height = Measure(width);
            var pixels = new byte[width * height * 4];
            Render(width, height, IntPtr.Zero, pixels);
            return pixels;
        }

        /// <summary>Draws the panel into an off-screen bitmap, then copies it to <paramref name="hdc"/>
        /// (a window) or into <paramref name="copy"/>.</summary>
        private void Render(int width, int height, IntPtr hdc, byte[]? copy)
        {
            _w = width;
            _h = height;
            Fonts();
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = _w,
                biHeight = -_h,
                biPlanes = 1,
                biBitCount = 32,
            };
            var mem = CreateCompatibleDC(hdc);
            var bmp = CreateDIBSection(hdc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            var old = SelectObject(mem, bmp);
            try
            {
                _bits = (byte*)bits;
                _draw = true;
                Layout();
                GdiFlush();
                SetBkMode(mem, TRANSPARENT);
                foreach (var (text, rect, color, font, format) in _texts)
                {
                    SelectObject(mem, font);
                    SetTextColor(mem, color);
                    var r = rect;
                    DrawTextW(mem, text, -1, ref r, format | DT_SINGLELINE | DT_NOPREFIX);
                }
                GdiFlush();
                if (copy != null)
                    System.Runtime.InteropServices.Marshal.Copy(bits, copy, 0, Math.Min(copy.Length, _w * _h * 4));
                else
                    BitBlt(hdc, 0, 0, _w, _h, mem, 0, 0, SRCCOPY);
            }
            finally
            {
                _bits = null;
                _draw = false;
                SelectObject(mem, old);
                DeleteObject(bmp);
                DeleteDC(mem);
            }
        }

        private void Fonts()
        {
            var dpi = (uint)Math.Round(_k * 96);
            if (dpi == _fontDpi && _font != IntPtr.Zero)
                return;
            foreach (var f in new[] { _font, _fontSmall, _fontTitle, _fontSection })
                if (f != IntPtr.Zero)
                    DeleteObject(f);
            IntPtr Font(double sizeDip, int weight) =>
                CreateFontW(-Px(sizeDip), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
            _font = Font(14, FW_NORMAL);
            _fontSmall = Font(12, FW_NORMAL);
            _fontTitle = Font(15, FW_SEMIBOLD);
            _fontSection = Font(12, FW_SEMIBOLD);
            _fontDpi = dpi;
        }

        /// <summary>Lays the panel out (and draws it when painting). Returns its height in pixels.</summary>
        private int Layout()
        {
            _hot.Clear();
            _texts.Clear();
            var s = _settings();
            var pad = Px(16);
            var left = pad;
            var right = _w - pad;
            if (_draw)
                FillRound(0, 0, _w, _h, 0, _t.Background);

            // header: the icon, the name, a hint
            var y = pad;
            var iconPx = Px(28);
            if (_draw)
                Blit(left, y, iconPx, TrayArt.Icon(iconPx, Palettes.Lut(s)));
            var tx = left + iconPx + Px(10);
            Text("TC001 Max Thermal Webcam", new RECT(tx, y - Px(1), right, y + Px(16)), _t.Text, _fontTitle, DT_LEFT);
            Text("Changes apply to the live picture", new RECT(tx, y + Px(15), right, y + Px(30)), _t.Dim, _fontSmall, DT_LEFT);
            y += iconPx + Px(10);

            y = Section("Overlay", y, left, right);
            y = Toggle("Show overlay", s.Overlay, true, () => Set("overlay", Bool(!s.Overlay)), y, left, right, 0);
            var marks = s.Overlay;
            var indent = Px(14);
            y = Toggle("Centre crosshair", s.Crosshair, marks, () => Set("crosshair", Bool(!s.Crosshair)), y, left, right, indent);
            y = Toggle("Hot spot marker", s.HotSpot, marks, () => Set("hotspot", Bool(!s.HotSpot)), y, left, right, indent);
            y = Toggle("Temperature readouts", s.Readouts, marks, () => Set("readouts", Bool(!s.Readouts)), y, left, right, indent);
            y = Toggle("Temperature scale", s.Scale, marks, () => Set("scale", Bool(!s.Scale)), y, left, right, indent);

            y = Section("Colour", y, left, right);
            y = Swatches(s, y, left, right);
            y = CustomRow(s, y, left, right);
            var ranges = new[] { RangeMode.Auto, RangeMode.Locked, RangeMode.Manual };
            y = Segmented("Range", ["Auto", "Locked", "Manual"], Array.IndexOf(ranges, s.Range),
                i => Set("range", ranges[i].ToString().ToLowerInvariant()), y, left, right);
            y = Segmented("Units", ["°C", "°F"], s.Units == TempUnits.F ? 1 : 0, i => Set("units", i == 1 ? "F" : "C"), y, left, right);

            y = Section("Picture", y, left, right);
            var rotations = new[] { 0, 90, 180, 270 };
            y = Segmented("Rotate", ["0°", "90°", "180°", "270°"], Array.IndexOf(rotations, s.Rotate),
                i => Set("rotate", rotations[i].ToString(System.Globalization.CultureInfo.InvariantCulture)), y, left, right);
            y = Toggle("Mirror left-right", s.MirrorHorizontal, true, () => Set("mirror_horizontal", Bool(!s.MirrorHorizontal)), y, left, right, 0);
            y = Toggle("Mirror top-bottom", s.MirrorVertical, true, () => Set("mirror_vertical", Bool(!s.MirrorVertical)), y, left, right, 0);

            y = Section("Camera", y, left, right);
            y = Toggle("Automatic shutter", s.AutoShutter, true, () => Set("auto_shutter", s.AutoShutter ? "off" : "on"), y, left, right, 0);

            // footer: links
            y += Px(8);
            if (_draw)
                FillRound(0, y, _w, 1, 0, _t.Divider);
            y += Px(6);
            var linkH = Px(32);
            var x = left - Px(8);
            x = Link("Edit settings file", x, y, linkH, _editFile);
            x = Link("Open log", x, y, linkH, _openLog);
            var exitW = Px(56);
            Link("Exit", right + Px(8) - exitW, y, linkH, _exit, exitW);
            y += linkH + Px(8);
            return y;
        }

        private int Section(string title, int y, int left, int right)
        {
            y += Px(10);
            Text(title, new RECT(left, y, right, y + Px(20)), _t.Dim, _fontSection, DT_LEFT | DT_VCENTER);
            return y + Px(22);
        }

        private int Toggle(string label, bool on, bool enabled, Action click, int y, int left, int right, int indent)
        {
            var h = Px(34);
            var row = new RECT(left - Px(8), y, right + Px(8), y + h);
            var hovered = Hot(row, enabled ? click : null);
            if (_draw)
            {
                if (hovered)
                    FillRound(row.Left, row.Top, row.Width, row.Height, Px(5), _t.Hover);
                // the switch: a 40 x 20 pill on the right
                double sw = Px(40), sh = Px(20);
                var sx = right - sw;
                var sy = y + (h - sh) / 2.0;
                var knob = Px(12) + (hovered ? Px(2) : 0);
                if (on)
                {
                    FillRound(sx, sy, sw, sh, sh / 2, Fade(_t.Accent, enabled));
                    var kx = sx + sw - sh / 2 - knob / 2.0;
                    FillRound(kx, sy + (sh - knob) / 2, knob, knob, knob / 2.0, Fade(_t.OnAccent, enabled));
                }
                else
                {
                    FillRound(sx, sy, sw, sh, sh / 2, Fade(_t.SwitchBorder, enabled));
                    var b = Math.Max(1.0, _k);
                    FillRound(sx + b, sy + b, sw - 2 * b, sh - 2 * b, sh / 2 - b, hovered ? _t.Hover : _t.Background);
                    var kx = sx + sh / 2 - knob / 2.0;
                    FillRound(kx, sy + (sh - knob) / 2, knob, knob, knob / 2.0, Fade(_t.SwitchBorder, enabled));
                }
            }
            Text(label, new RECT(left + indent, y, right - Px(48), y + h), enabled ? _t.Text : _t.Disabled, _font, DT_LEFT | DT_VCENTER | DT_END_ELLIPSIS);
            return y + h;
        }

        private int Segmented(string label, string[] options, int selected, Action<int> click, int y, int left, int right)
        {
            var h = Px(36);
            var labelW = Px(76);
            Text(label, new RECT(left, y, left + labelW, y + h), _t.Text, _font, DT_LEFT | DT_VCENTER);
            var x0 = left + labelW;
            var bh = Px(28);
            var by = y + (h - bh) / 2;
            var total = right - x0;
            if (_draw)
                FillRound(x0, by, total, bh, Px(5), _t.Control);
            var inset = Px(2);
            for (var i = 0; i < options.Length; i++)
            {
                var sx = x0 + total * i / options.Length;
                var ex = x0 + total * (i + 1) / options.Length;
                var seg = new RECT(sx, by, ex, by + bh);
                var index = i;
                var hovered = Hot(seg, i == selected ? null : () => click(index));
                if (_draw)
                {
                    if (i == selected)
                        FillRound(sx + inset, by + inset, ex - sx - 2 * inset, bh - 2 * inset, Px(4), _t.Accent);
                    else if (hovered)
                        FillRound(sx + inset, by + inset, ex - sx - 2 * inset, bh - 2 * inset, Px(4), _t.ControlHover);
                }
                Text(options[i], seg, i == selected ? _t.OnAccent : _t.Text, _font, DT_CENTER | DT_VCENTER);
            }
            return y + h;
        }

        /// <summary>The palettes as a grid of previews, 3 per row; click one to use it.</summary>
        private int Swatches(WebcamSettings s, int y, int left, int right)
        {
            var names = Palettes.Names;
            const int cols = 3;
            var gap = Px(10);
            var tw = (right - left - gap * (cols - 1)) / cols;
            var swH = Px(20);
            var labelH = Px(18);
            var rowH = swH + labelH + Px(8);
            y += Px(4);
            for (var i = 0; i < names.Length; i++)
            {
                var x = left + i % cols * (tw + gap);
                var ty = y + i / cols * rowH;
                var name = names[i];
                var tile = new RECT(x - Px(4), ty - Px(4), x + tw + Px(4), ty + swH + labelH + Px(2));
                var selected = name == s.Palette;
                var hovered = Hot(tile, selected ? null : () => Set("palette", name));
                if (_draw)
                {
                    if (hovered)
                        FillRound(tile.Left, tile.Top, tile.Width, tile.Height, Px(6), _t.Hover);
                    if (selected)
                    {
                        FillRound(x - Px(3), ty - Px(3), tw + Px(6), swH + Px(6), Px(6), _t.Accent);
                        FillRound(x - Px(1), ty - Px(1), tw + Px(2), swH + Px(2), Px(5), _t.Background);
                    }
                    Gradient(x, ty, tw, swH, Palettes.Lut(s with { Palette = name }));
                }
                Text(Palettes.Label(name), new RECT(x - Px(4), ty + swH, x + tw + Px(4), ty + swH + labelH),
                     selected ? _t.Text : _t.Dim, _fontSmall, DT_CENTER | DT_VCENTER);
            }
            return y + (names.Length + cols - 1) / cols * rowH;
        }

        /// <summary>The custom palette's colours, cold to hot: click one to change it (Windows' colour
        /// dialog), + adds a colour, - removes the last. Any change also selects the custom palette.</summary>
        private int CustomRow(WebcamSettings s, int y, int left, int right)
        {
            var h = Px(40);
            var labelW = Px(76);
            Text("Custom", new RECT(left, y, left + labelW, y + h), _t.Text, _font, DT_LEFT | DT_VCENTER);
            var stops = s.CustomPalette;
            var chip = Px(26);
            var gap = Px(8);
            var cy = y + (h - chip) / 2;
            var x = left + labelW;
            for (var i = 0; i < stops.Length; i++)
            {
                var index = i;
                var r = new RECT(x - Px(3), cy - Px(3), x + chip + Px(3), cy + chip + Px(3));
                var hovered = Hot(r, () => PickStop(index));
                if (_draw)
                {
                    if (hovered)
                        FillRound(r.Left, r.Top, r.Width, r.Height, Px(7), _t.Hover);
                    var (cr, cg, cb) = Palettes.ParseHex(stops[i]);
                    FillRound(x, cy, chip, chip, Px(6), _t.SwitchBorder);
                    FillRound(x + Px(1), cy + Px(1), chip - Px(2), chip - Px(2), Px(5), new Rgb(cr, cg, cb));
                }
                x += chip + gap;
            }
            // + and - on the right
            var bw = Px(28);
            var minus = new RECT(right - bw, cy - Px(1), right, cy + chip + Px(1));
            var plus = new RECT(right - 2 * bw - Px(6), cy - Px(1), right - bw - Px(6), cy + chip + Px(1));
            Button("+", plus, stops.Length < 5 ? AddStop : null);
            Button("\u2212", minus, stops.Length > 2 ? RemoveStop : null);
            return y + h;
        }

        private void Button(string text, RECT r, Action? click)
        {
            var hovered = Hot(r, click);
            if (_draw)
                FillRound(r.Left, r.Top, r.Width, r.Height, Px(5), hovered ? _t.ControlHover : _t.Control);
            Text(text, r, click == null ? _t.Disabled : _t.Text, _fontTitle, DT_CENTER | DT_VCENTER);
        }

        private void PickStop(int index)
        {
            var stops = _settings().CustomPalette.ToArray();
            if (PickColour(stops[index]) is { } hex)
            {
                stops[index] = hex;
                SetCustom(stops);
            }
        }

        private void AddStop()
        {
            var stops = _settings().CustomPalette;
            if (stops.Length < 5 && PickColour(stops[^1]) is { } hex)
                SetCustom([.. stops, hex]);
        }

        private void RemoveStop()
        {
            var stops = _settings().CustomPalette;
            if (stops.Length > 2)
                SetCustom(stops[..^1]);
        }

        private void SetCustom(string[] stops) => _set(["palette=custom", "custom_palette=" + string.Join(",", stops)]);

        /// <summary>Windows' colour dialog, starting at <paramref name="initial"/>; the chosen colour
        /// as #rrggbb, or null if cancelled. Its 16 "custom colours" start as the preset palettes'
        /// stops and keep your additions while the tray icon runs.</summary>
        private string? PickColour(string initial)
        {
            if (!_coloursSeeded)
            {
                var seeds = Palettes.Presets.Values.SelectMany(v => v).Concat(_settings().CustomPalette).Distinct().Take(16).ToArray();
                for (var i = 0; i < seeds.Length; i++)
                    CustomColours[i] = Ref(ToRgb(seeds[i]));
                _coloursSeeded = true;
            }
            var cc = new CHOOSECOLORW
            {
                lStructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CHOOSECOLORW>(),
                hwndOwner = _hwnd,
                rgbResult = Ref(ToRgb(initial)),
                Flags = CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR,
            };
            bool ok;
            _picking = true;
            try
            {
                fixed (uint* custom = CustomColours)
                {
                    cc.lpCustColors = (IntPtr)custom;
                    ok = ChooseColorW(ref cc);
                }
            }
            finally
            {
                _picking = false;
            }
            SetForegroundWindow(_hwnd); // back to the panel
            if (!ok)
                return null;
            var c = cc.rgbResult;
            return $"#{c & 0xFF:x2}{(c >> 8) & 0xFF:x2}{(c >> 16) & 0xFF:x2}";
        }

        private static Rgb ToRgb(string hex)
        {
            var (r, g, b) = Palettes.ParseHex(hex);
            return new Rgb(r, g, b);
        }

        /// <summary>A rounded preview of a palette table, cold on the left.</summary>
        private void Gradient(int x, int y, int w, int h, byte[] lut) =>
            FillRound(x, y, w, h, Px(4), default, px =>
            {
                var j = Math.Clamp((int)((px - x) * 255.0 / Math.Max(1, w - 1)), 0, 255) * 3;
                return new Rgb(lut[j], lut[j + 1], lut[j + 2]);
            });

        private int Link(string text, int x, int y, int h, Action click, int? width = null)
        {
            var w = width ?? Px(text.Length * 7 + 18);
            var r = new RECT(x, y, x + w, y + h);
            var hovered = Hot(r, click);
            if (_draw && hovered)
                FillRound(r.Left, r.Top, r.Width, r.Height, Px(5), _t.Hover);
            Text(text, r, _t.AccentText, _font, DT_CENTER | DT_VCENTER);
            return x + w;
        }

        /// <summary>Records a clickable area (null: not clickable); true if the mouse is over it.</summary>
        private bool Hot(RECT r, Action? click)
        {
            if (click == null)
                return false;
            _hot.Add((r, click));
            return _hot.Count - 1 == _hover;
        }

        private void Text(string text, RECT r, Rgb color, IntPtr font, uint format)
        {
            if (_draw)
                _texts.Add((text, r, Ref(color), font, format));
        }

        private void Set(string key, string value) => _set([$"{key}={value}"]);

        private static string Bool(bool b) => b ? "true" : "false";

        private int Px(double dip) => (int)Math.Round(dip * _k);

        private Rgb Fade(Rgb c, bool enabled) => enabled ? c : Rgb.Mix(c, _t.Background, 0.55);

        private static uint Ref(Rgb c) => (uint)(c.R | (c.G << 8) | (c.B << 16)); // COLORREF 0x00bbggrr

        // -- pixels --------------------------------------------------------------------------------
        /// <summary>Fills a rounded rectangle with anti-aliased edges (coverage from the signed
        /// distance at each pixel centre), in one colour or a colour per column.</summary>
        private void FillRound(double x, double y, double w, double h, double r, Rgb color, Func<int, Rgb>? colorAt = null)
        {
            if (_bits == null || w <= 0 || h <= 0)
                return;
            r = Math.Min(r, Math.Min(w, h) / 2);
            double cx = x + w / 2, cy = y + h / 2, hw = w / 2 - r, hh = h / 2 - r;
            int x0 = Math.Max(0, (int)Math.Floor(x)), x1 = Math.Min(_w, (int)Math.Ceiling(x + w));
            int y0 = Math.Max(0, (int)Math.Floor(y)), y1 = Math.Min(_h, (int)Math.Ceiling(y + h));
            for (var py = y0; py < y1; py++)
            {
                var dy = Math.Abs(py + 0.5 - cy) - hh;
                for (var px = x0; px < x1; px++)
                {
                    var dx = Math.Abs(px + 0.5 - cx) - hw;
                    var d = Math.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0) + Math.Max(dy, 0) * Math.Max(dy, 0))
                            + Math.Min(Math.Max(dx, dy), 0) - r;
                    var a = Math.Clamp(0.5 - d, 0, 1);
                    if (a <= 0)
                        continue;
                    var c = colorAt?.Invoke(px) ?? color;
                    var p = _bits + (py * _w + px) * 4;
                    p[0] = (byte)(p[0] + (c.B - p[0]) * a);
                    p[1] = (byte)(p[1] + (c.G - p[1]) * a);
                    p[2] = (byte)(p[2] + (c.R - p[2]) * a);
                    p[3] = 255;
                }
            }
        }

        /// <summary>Draws premultiplied BGRA pixels (n x n) over the bitmap.</summary>
        private void Blit(int x, int y, int n, byte[] src)
        {
            if (_bits == null)
                return;
            for (var j = 0; j < n; j++)
            {
                var py = y + j;
                if (py < 0 || py >= _h) continue;
                for (var i = 0; i < n; i++)
                {
                    var px = x + i;
                    if (px < 0 || px >= _w) continue;
                    var s = (j * n + i) * 4;
                    var inv = 255 - src[s + 3];
                    var p = _bits + (py * _w + px) * 4;
                    p[0] = (byte)(src[s] + p[0] * inv / 255);
                    p[1] = (byte)(src[s + 1] + p[1] * inv / 255);
                    p[2] = (byte)(src[s + 2] + p[2] * inv / 255);
                }
            }
        }

        // -- colours -------------------------------------------------------------------------------
        internal readonly record struct Rgb(byte R, byte G, byte B)
        {
            public static Rgb Mix(Rgb a, Rgb b, double t) =>
                new((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        /// <summary>Windows 11 flyout colours for the light or dark app mode, with the accent colour.</summary>
        internal sealed record Theme(bool IsDark, Rgb Background, Rgb Hover, Rgb Control, Rgb ControlHover, Rgb Text, Rgb Dim,
                                     Rgb Disabled, Rgb Divider, Rgb Border, Rgb SwitchBorder, Rgb Accent, Rgb OnAccent, Rgb AccentText)
        {
            internal static readonly Rgb DefaultAccent = new(0, 120, 212);

            public static readonly Theme Dark = Make(true, DefaultAccent);

            public static Theme Current()
            {
                var dark = true;
                var accent = DefaultAccent;
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                        if (k?.GetValue("AppsUseLightTheme") is int light)
                            dark = light == 0;
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                        if (k?.GetValue("AccentColor") is int abgr)
                            accent = new Rgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
                {
                }
                return Make(dark, accent);
            }

            internal static Theme Make(bool dark, Rgb accent)
            {
                var white = new Rgb(255, 255, 255);
                var black = new Rgb(0, 0, 0);
                if (dark)
                {
                    var a = Rgb.Mix(accent, white, 0.45); // Windows shows a lighter accent on dark
                    return new Theme(true, new(40, 40, 40), new(52, 52, 52), new(28, 28, 28), new(58, 58, 58), white, new(166, 166, 166),
                                     new(110, 110, 110), new(60, 60, 60), new(70, 70, 70), new(200, 200, 200), a, black, a);
                }
                var l = Rgb.Mix(accent, black, 0.15);
                return new Theme(false, new(249, 249, 249), new(236, 236, 236), new(232, 232, 232), new(220, 220, 220), new(27, 27, 27),
                                 new(96, 96, 96), new(160, 160, 160), new(225, 225, 225), new(205, 205, 205), new(110, 110, 110), l, white, l);
            }
        }
    }
}
