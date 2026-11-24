// TC001 Max Thermal Webcam tray: an icon in the notification area. Clicking it opens a small settings
// window (Panel.cs) that stays open while you change several settings; right-clicking it gives a
// short menu (settings, check for updates, edit the file, the log, exit). Once a day it looks for a newer
// release on GitHub and offers a one-click update (Updates.cs; setting update_check). Every choice is written to C:\ProgramData\TC001MaxWebcam\settings.json through the same
// strict code as "TC001MaxWebcam settings set" (Core\Settings.cs), and the webcam applies it within
// about a second, even while an app is streaming. The file is re-read every 2 s, so changes made
// elsewhere show up; the icon takes the colours of the current palette.
//
//   TC001MaxWebcamTray.exe     starts the icon (once per sign-in; a second start exits at once)
//
// Started at sign-in by the installer's HKLM Run entry. Exits when asked by the menu, by Windows
// (sign-out, or Restart Manager during an install), or by the updater (Shared.TrayExitEventName).
// Never opens the camera. Origin: written for this project.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using TC001MaxWebcam.Core;
using static TC001MaxWebcam.Tray.Native;

namespace TC001MaxWebcam.Tray
{
    internal static class TrayApp
    {
        private const string Title = "TC001 Max Thermal Webcam";
        private const uint CallbackMessage = WM_APP + 1;
        private const uint ExitMessage = WM_APP + 2;
        private const uint UpdateMessage = WM_APP + 3;   // Updates.cs: its state changed
        private const uint IconId = 1;

        // right-click menu command ids
        private const uint CmdSettings = 1, CmdEditFile = 2, CmdOpenLog = 3, CmdExit = 4, CmdUpdates = 5;

        // the first automatic update check waits this long after start (sign-in is busy enough)
        private const long FirstCheckDelayMs = 60_000;

        private static WndProc? _proc; // kept alive: Windows calls it
        private static IntPtr _hwnd;
        private static IntPtr _icon;
        private static uint _taskbarCreated;
        private static (bool, DateTime, long) _stamp;
        private static WebcamSettings _settings = WebcamSettings.Default;
        private static string _iconPalette = "";
        private static Panel? _panel;
        private static Updates? _updates;
        private static long _startedAt;

        [STAThread]
        private static int Main()
        {
            using var mutex = new Mutex(true, Shared.TrayMutexName, out var first);
            if (!first)
                return 0; // already running in this session
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            RegisterApplicationRestart(null, 0); // Restart Manager starts it again after an install

            _proc = WindowProc;
            var instance = GetModuleHandleW(IntPtr.Zero);
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = instance,
                lpszClassName = "TC001MaxWebcamTray",
            };
            if (RegisterClassExW(ref wc) == 0)
                return 1;
            // a hidden top-level window (never shown): it receives the icon's messages, and the session
            // end / Restart Manager messages that a message-only window would not
            _hwnd = CreateWindowExW(0, wc.lpszClassName, Title, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                return 1;
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

            using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Shared.TrayExitEventName);
            var hwnd = _hwnd;
            var watcher = new Thread(() =>
            {
                exitEvent.WaitOne();
                PostMessageW(hwnd, ExitMessage, IntPtr.Zero, IntPtr.Zero);
            }) { IsBackground = true, Name = "exit request" };
            watcher.Start();

            Log.Sink = TrayLog;
            ReloadSettings(force: true);
            _startedAt = Environment.TickCount64;
            _updates = new Updates(() => PostMessageW(hwnd, UpdateMessage, IntPtr.Zero, IntPtr.Zero));
            _panel = new Panel(() => _settings, Set, EditFile, () => Open(LogPath),
                               () => PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero),
                               () => _updates.View, r => _updates.Install(r), () => _updates.MaybeCheck(true, manual: true));
            AddIcon();
            SetTimer(_hwnd, 1, 2000, IntPtr.Zero); // notice settings changed elsewhere (icon colours, tip)

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            RemoveIcon();
            return 0;
        }

        private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case CallbackMessage:
                    // NOTIFYICON_VERSION_4: the event is in the low word of lParam, the anchor in wParam
                    var ev = (uint)(lParam.ToInt64() & 0xFFFF);
                    if (ev is NIN_SELECT or NIN_KEYSELECT or NIN_BALLOONUSERCLICK)
                    {
                        if (ev == NIN_BALLOONUSERCLICK && _panel is { Visible: true })
                            return IntPtr.Zero;
                        ReloadSettings(force: true);
                        _panel?.Toggle(_hwnd, IconId);
                    }
                    else if (ev == WM_CONTEXTMENU)
                    {
                        _panel?.Hide();
                        ShowMenu((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                    }
                    return IntPtr.Zero;
                case WM_TIMER:
                    if (ReloadSettings(force: false))
                    {
                        UpdateIcon();
                        _panel?.Refresh();
                    }
                    if (Environment.TickCount64 - _startedAt > FirstCheckDelayMs)
                        _updates?.MaybeCheck(_settings.UpdateCheck, manual: false);
                    return IntPtr.Zero;
                case UpdateMessage:
                    _panel?.Refresh();
                    if (_updates?.Notify is { } release)
                    {
                        _updates.Notify = null;
                        Balloon($"TC001 Max Thermal Webcam {ReleaseCheck.Format(release.Version)} is available",
                                "Click to open the settings window, then Update. Windows asks for administrator rights once.");
                    }
                    return IntPtr.Zero;
                case ExitMessage:
                case WM_CLOSE:
                    DestroyWindow(hWnd);
                    return IntPtr.Zero;
                case WM_QUERYENDSESSION:
                    return 1; // fine to end
                case WM_ENDSESSION:
                    if (wParam != IntPtr.Zero)
                    {
                        RemoveIcon();
                        Environment.Exit(0);
                    }
                    return IntPtr.Zero;
                case WM_DESTROY:
                    RemoveIcon();
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                AddIcon(); // Explorer restarted: the icon must be added again
                return IntPtr.Zero;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        // -- the icon ------------------------------------------------------------------------------
        private static NOTIFYICONDATAW Data(uint flags) => new()
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = Tip(),
            szInfo = "",
            szInfoTitle = "",
        };

        private static void AddIcon()
        {
            MakeIcon();
            var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            Shell_NotifyIconW(NIM_DELETE, ref d); // after an Explorer restart, or a stale entry
            Shell_NotifyIconW(NIM_ADD, ref d);
            d.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, ref d);
        }

        private static void UpdateIcon()
        {
            if (_iconPalette != PaletteKey(_settings))
                MakeIcon();
            var d = Data(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            Shell_NotifyIconW(NIM_MODIFY, ref d);
        }

        /// <summary>A Windows notification from the icon (held back during quiet time and full-screen apps).</summary>
        private static void Balloon(string title, string text)
        {
            var d = Data(NIF_INFO);
            d.szInfoTitle = title.Length > 63 ? title[..63] : title;
            d.szInfo = text.Length > 255 ? text[..255] : text;
            d.dwInfoFlags = NIIF_INFO | NIIF_RESPECT_QUIET_TIME;
            Shell_NotifyIconW(NIM_MODIFY, ref d);
        }

        private static void RemoveIcon()
        {
            if (_hwnd == IntPtr.Zero)
                return;
            var d = Data(0);
            Shell_NotifyIconW(NIM_DELETE, ref d);
        }

        private static void MakeIcon()
        {
            var n = Math.Clamp(GetSystemMetrics(SM_CXSMICON), 16, 64);
            var old = _icon;
            _icon = Native.Icon(n, TrayArt.Icon(n, Palettes.Lut(_settings)));
            _iconPalette = PaletteKey(_settings);
            if (old != IntPtr.Zero)
                DestroyIcon(old);
        }

        private static string PaletteKey(WebcamSettings s) => s.Palette == "custom" ? "custom:" + string.Join(",", s.CustomPalette) : s.Palette;

        private static string Tip()
        {
            var s = _settings;
            var tip = $"{Title}\n{Palettes.Label(s.Palette)} palette, {s.UnitSymbol}, rotate {s.Rotate}°" +
                      (s.Overlay ? "" : ", overlay off");
            return tip.Length > 127 ? tip[..127] : tip;
        }

        // -- settings ------------------------------------------------------------------------------
        /// <summary>Re-reads settings.json if it changed (or <paramref name="force"/>). True if it did.</summary>
        private static bool ReloadSettings(bool force)
        {
            var path = Shared.SettingsPath;
            (bool, DateTime, long) stamp;
            try
            {
                var info = new FileInfo(path);
                stamp = info.Exists ? (true, info.LastWriteTimeUtc, info.Length) : (false, default, 0);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            if (!force && stamp == _stamp)
                return false;
            try
            {
                _settings = SettingsFile.Load(path, []);
                _stamp = stamp;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false; // being replaced: try again on the next tick
            }
        }

        /// <summary>Applies "key=value" <paramref name="assignments"/> through the strict "settings set"
        /// code (all or nothing) and writes the file.</summary>
        private static void Set(string[] assignments)
        {
            ReloadSettings(force: true);
            try
            {
                if (!Directory.Exists(Shared.DataDir))
                    throw new SettingsException($"{Shared.DataDir} does not exist. Install the webcam first.");
                var next = SettingsFile.Apply(_settings, assignments);
                SettingsFile.Save(Shared.SettingsPath, next);
            }
            catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException)
            {
                MessageBoxW(_hwnd, $"Could not change the settings ({string.Join(", ", assignments)}):\n\n{e.Message}", Title, MB_ICONWARNING);
            }
            ReloadSettings(force: true);
            UpdateIcon();
            _panel?.Refresh();
        }

        // -- the right-click menu ------------------------------------------------------------------
        private static void ShowMenu(int x, int y)
        {
            var menu = CreatePopupMenu();
            try
            {
                Item(menu, CmdSettings, "Settings...");
                SetMenuDefaultItem(menu, CmdSettings, 0);
                var upd = _updates?.View;
                Item(menu, CmdUpdates, upd is { State: UpdateState.Available, Release: { } rel } ? $"Update to {ReleaseCheck.Format(rel.Version)}..." : "Check for updates",
                     grayed: _updates?.Busy == true);
                Separator(menu);
                Item(menu, CmdEditFile, "Edit settings file...");
                Item(menu, CmdOpenLog, "Open webcam log", grayed: !File.Exists(LogPath));
                Separator(menu);
                Item(menu, CmdExit, "Exit tray icon");

                SetForegroundWindow(_hwnd); // so the menu closes when you click elsewhere
                var cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, _hwnd, IntPtr.Zero);
                PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                switch (cmd)
                {
                    case CmdSettings: ReloadSettings(force: true); _panel?.Toggle(_hwnd, IconId); break;
                    case CmdEditFile: EditFile(); break;
                    case CmdOpenLog: Open(LogPath); break;
                    case CmdUpdates:
                        if (upd is { State: UpdateState.Available, Release: { } r })
                            _updates?.Install(r);
                        else
                            _updates?.MaybeCheck(true, manual: true);
                        ReloadSettings(force: true);
                        if (_panel is { Visible: false })
                            _panel.Toggle(_hwnd, IconId);
                        break;
                    case CmdExit: PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); break;
                }
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        private static string LogPath => Path.Combine(Shared.DataDir, Shared.LogName);

        /// <summary>The tray's own log (update checks and downloads): %LOCALAPPDATA%\TC001MaxWebcam\tray.log.</summary>
        private static void TrayLog(string level, string message)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TC001MaxWebcam");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "tray.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Delete(path);
                File.AppendAllText(path, $"{Log.Stamp()} {level,-5} {message}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        private static void EditFile()
        {
            var path = Shared.SettingsPath;
            try
            {
                if (!File.Exists(path) && Directory.Exists(Shared.DataDir))
                    SettingsFile.Save(path, _settings); // every key, so there is something to edit
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                MessageBoxW(_hwnd, $"Could not create {path}:\n\n{e.Message}", Title, MB_ICONWARNING);
                return;
            }
            Open(path);
        }

        private static void Open(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { ArgumentList = { path }, UseShellExecute = false });
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
            {
                MessageBoxW(_hwnd, $"Could not open {path}:\n\n{e.Message}", Title, MB_ICONWARNING);
            }
        }

        // -- menu helpers --------------------------------------------------------------------------
        private static void Item(IntPtr menu, uint id, string text, bool check = false, bool grayed = false) =>
            AppendMenuW(menu, MF_STRING | (check ? MF_CHECKED : 0) | (grayed ? MF_GRAYED : 0), (IntPtr)id, text);

        private static void Separator(IntPtr menu) => AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
    }
}
