// TC001MaxWebcam.exe update: the one-click updater (README "Updating").
//
//   update [--from PATH] [--check] [--now] [--pause]
//
// 1. Finds the newest TC001MaxWebcam-*.msi of this product (its UpgradeCode) in the update source:
//    --from (a folder or one .msi; remembered for next time in HKCU\Software\TC001MaxWebcam
//    UpdateSource, except with --check), else the remembered folder, else the folder the installed .msi was run from (the
//    installer writes HKLM\SOFTWARE\TC001MaxWebcam InstalledFrom). --check stops here.
// 2. Waits until no app streams from the webcam (webcam.log and the webcam's camera claim; --now
//    skips the wait: the app's picture then stops).
// 3. Copies this program to %TEMP%\TC001MaxWebcam-update and starts the copy elevated (one UAC
//    prompt) with "update --apply <msi> --parent <pid>", then exits: the installer must be free to
//    replace C:\Program Files\TC001MaxWebcam\TC001MaxWebcam.exe, which a running update would hold.
// 4. The elevated copy waits for this process to end, checks the package again, stops the Frame
//    Server services (they keep the old media source DLL loaded after any use; Windows starts them
//    again on demand, as install.ps1 -RestartFrameServer does) and runs
//    msiexec /i <msi> /passive /norestart /l*v %TEMP%\TC001MaxWebcam-update-<version>.log
//    (a major upgrade: settings, the data folder and the virtual camera stay).
//    Before msiexec it asks the tray icon (TC001MaxWebcamTray.exe) to exit, so its files are free,
//    and afterwards starts it again through Explorer, so that it runs as the signed-in user and not
//    elevated.
//
// The decisions are in Core\UpdateRules.cs (checked by the self-test); this file is the Windows side.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using TC001MaxWebcam.Core;

namespace TC001MaxWebcam.Cli
{
    internal static class Updater
    {
        private const string HklmKey = @"SOFTWARE\TC001MaxWebcam";   // written by the installer
        private const string HkcuKey = @"Software\TC001MaxWebcam";
        private const string InstalledFromValue = "InstalledFrom";
        private const string UpdateSourceValue = "UpdateSource";
        private const string CopyFolderName = "TC001MaxWebcam-update";

        public static int Run(string[] args)
        {
            var pause = Has(args, "--pause");
            var code = RunInner(args, out var launched);
            if (pause && !launched)
            {
                Console.WriteLine();
                Console.Write("Press Enter to close.");
                Console.ReadLine();
            }
            return code;
        }

        private static int RunInner(string[] args, out bool launched)
        {
            launched = false;
            if (Value(args, "--apply") is { } apply)
                return Apply(apply, Value(args, "--parent"), Has(args, "--now"));

            var from = Value(args, "--from");
            var installed = InstalledVersion();
            Console.WriteLine($"installed   : {installed?.ToString() ?? "not installed by the .msi"}");
            var src = UpdateRules.Source(from, ReadString(Registry.CurrentUser, HkcuKey, UpdateSourceValue),
                                         ReadString(Registry.LocalMachine, HklmKey, InstalledFromValue),
                                         Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (src is not { } source)
            {
                Console.Error.WriteLine("No update source known yet. Point it at the repository (build.ps1 puts the .msi at its top), once:");
                Console.Error.WriteLine(@"  TC001MaxWebcam update --from C:\path\to\TC001-Max-Webcam");
                return 2;
            }
            var path = Path.GetFullPath(source.Path);
            Console.WriteLine($"looking in  : {path} ({source.Origin})");
            if (!File.Exists(path) && !Directory.Exists(path))
                return Fail($"{path} does not exist (another folder: --from PATH)", 1);
            if (from != null && !Has(args, "--check"))
                Remember(path);

            var newest = UpdateRules.Newest(UpdateRules.MsiFiles(path).Select(ReadMsi));
            if (newest is not { } n)
                return Fail($"no TC001 Max Thermal Webcam installer ({UpdateRules.MsiPattern}) there", 1);
            Console.WriteLine($"newest there: {n.Version} ({Path.GetFileName(n.Msi.Path)})");
            if (!UpdateRules.IsNewer(n.Version, installed))
            {
                Console.WriteLine("Up to date.");
                return 0;
            }
            if (Has(args, "--check"))
            {
                Console.WriteLine($"Update available: {installed?.ToString() ?? "(none)"} -> {n.Version}. Run: TC001MaxWebcam update");
                return 0;
            }

            if (!Has(args, "--now"))
                WaitUntilIdle();

            // run the rest from a copy, so that the installer can replace this program's files
            var copyDir = Path.Combine(Path.GetTempPath(), CopyFolderName);
            try
            {
                if (Directory.Exists(copyDir))
                    Directory.Delete(copyDir, recursive: true);
                CopyDirectory(AppContext.BaseDirectory, copyDir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Fail($"cannot prepare {copyDir}: {e.Message} (an earlier update window still open? close it and try again)", 1);
            }
            var exe = Path.Combine(copyDir, Path.GetFileName(Environment.ProcessPath ?? "TC001MaxWebcam.exe"));
            var arguments = $"update --apply \"{n.Msi.Path}\" --parent {Environment.ProcessId}{(Has(args, "--now") ? " --now" : "")}";
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas" });
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                return Fail("Cancelled at the administrator prompt; nothing changed.", 1);
            }
            launched = true;
            Console.WriteLine($"Updating to {n.Version}: it continues in the administrator window that just opened.");
            return 0;
        }

        // -- the elevated part ---------------------------------------------------------------------
        private static int Apply(string msi, string? parent, bool now)
        {
            try { Console.Title = "Updating TC001 Max Thermal Webcam"; } catch (IOException) { }
            var code = 1;
            try
            {
                code = ApplyInner(msi, parent, now);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("error: " + e.Message);
            }
            Console.WriteLine();
            Console.Write("Press Enter to close.");
            Console.ReadLine();
            return code;
        }

        private static int ApplyInner(string msi, string? parent, bool now)
        {
            if (int.TryParse(parent, out var parentPid))
            {
                try
                {
                    using var p = Process.GetProcessById(parentPid);
                    p.WaitForExit(10_000);
                }
                catch (ArgumentException) { } // already gone
            }
            if (!IsElevated())
                return Fail("the update must run elevated (it is started that way by \"TC001MaxWebcam update\").", 3);

            var info = ReadMsi(msi);
            var installed = InstalledVersion();
            if (UpdateRules.Newest([info]) is not { } n)
                return Fail($"{msi} is not a TC001 Max Thermal Webcam installer; nothing changed.", 1);
            if (!UpdateRules.IsNewer(n.Version, installed))
                return Fail($"{n.Version} is not newer than the installed {installed}; nothing changed.", 1);
            Console.WriteLine($"Updating TC001 Max Thermal Webcam {installed?.ToString() ?? "(not installed)"} -> {n.Version}");
            Console.WriteLine($"  from {msi}");

            if (!now)
                WaitUntilIdle();

            // the Frame Server keeps the old media source loaded after any use: stop it (Windows starts it
            // again when an app next opens a camera), so the files can be replaced without a restart
            foreach (var service in new[] { "FrameServerMonitor", "FrameServer" })
                Console.WriteLine($"  {service}: {Services.Stop(service)}");

            var trayWasRunning = StopTray();

            var log = Path.Combine(Path.GetTempPath(), $"TC001MaxWebcam-update-{n.Version}.log");
            Console.WriteLine($"  msiexec /i ... /passive (log: {log})");
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = false };
            foreach (var a in new[] { "/i", msi, "/passive", "/norestart", "/l*v", log })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("msiexec did not start");
            proc.WaitForExit();
            var (ok, text) = UpdateRules.DescribeMsiExit(proc.ExitCode);
            Console.WriteLine();
            Console.WriteLine(ok ? $"Done: {text}. Installed now: {InstalledVersion()?.ToString() ?? "?"}." : $"Not updated: {text}. Details: {log}");
            if (ok)
                Console.WriteLine("Open the camera app again; the settings are unchanged.");
            if (trayWasRunning)
                StartTray();
            return ok ? 0 : 1;
        }

        // -- the tray icon -------------------------------------------------------------------------
        /// <summary>Asks the tray icon to exit (its exit event) and waits up to 5 s. True if it ran.</summary>
        private static bool StopTray()
        {
            if (!EventWaitHandle.TryOpenExisting(Shared.TrayExitEventName, out var exit))
                return false;
            using (exit)
            {
                Console.WriteLine("  tray icon: asking it to exit");
                exit.Set();
            }
            var name = Path.GetFileNameWithoutExtension(Shared.TrayExeName);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && Process.GetProcessesByName(name).Length > 0)
                Thread.Sleep(100);
            return true;
        }

        /// <summary>Starts the installed tray icon again. Through explorer.exe, which runs it with the
        /// signed-in user's own (not elevated) rights, as at sign-in.</summary>
        private static void StartTray()
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TC001MaxWebcam", Shared.TrayExeName);
            if (!File.Exists(exe))
                return;
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
                psi.ArgumentList.Add(exe);
                using var _ = Process.Start(psi);
                Console.WriteLine("  tray icon: started again");
            }
            catch (Exception e) when (e is Win32Exception or IOException)
            {
                Console.WriteLine($"  tray icon: not started ({e.Message}); it starts at the next sign-in");
            }
        }

        // -- shared helpers ------------------------------------------------------------------------
        /// <summary>The installed version (Windows Installer's record for this UpgradeCode), or null.</summary>
        public static Version? InstalledVersion()
        {
            Version? best = null;
            for (uint i = 0; ; i++)
            {
                var product = new StringBuilder(39);
                if (Msi.MsiEnumRelatedProductsW(UpdateRules.UpgradeCode, 0, i, product) != 0)
                    break;
                var v = UpdateRules.ParseVersion(Msi.ProductInfo(product.ToString(), "VersionString"));
                if (v != null && (best == null || v > best))
                    best = v;
            }
            return best;
        }

        /// <summary>The package's UpgradeCode and ProductVersion (nulls if it cannot be read).</summary>
        public static MsiInfo ReadMsi(string path)
        {
            string? upgrade = null, version = null;
            try
            {
                foreach (var (name, value) in Msi.Properties(path))
                {
                    if (name == "UpgradeCode") upgrade = value;
                    else if (name == "ProductVersion") version = value;
                }
            }
            catch (Win32Exception) { }
            return new MsiInfo(path, upgrade, version);
        }

        /// <summary>One line for "status": whether a newer installer waits in the update source.</summary>
        public static string StatusLine()
        {
            var src = UpdateRules.Source(null, ReadString(Registry.CurrentUser, HkcuKey, UpdateSourceValue),
                                         ReadString(Registry.LocalMachine, HklmKey, InstalledFromValue),
                                         Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (src is not { } s)
                return "no update source yet (TC001MaxWebcam update --from <folder with the .msi>)";
            if (!File.Exists(s.Path) && !Directory.Exists(s.Path))
                return $"update source {s.Path} does not exist";
            var installed = InstalledVersion();
            var newest = UpdateRules.Newest(UpdateRules.MsiFiles(s.Path).Select(ReadMsi));
            if (newest is not { } n)
                return $"no installer in {s.Path}";
            return UpdateRules.IsNewer(n.Version, installed)
                ? $"{n.Version} available in {s.Path}: TC001MaxWebcam update"
                : $"up to date ({installed}; newest in {s.Path}: {n.Version})";
        }

        /// <summary>Whether an app streams from the webcam now (its camera claim, or webcam.log).</summary>
        private static bool Streaming()
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                var host = System.Net.Dns.GetHostName();
                if (Directory.Exists(Shared.ClaimDir) &&
                    Claims.Read(Shared.ClaimDir).Any(c => c.Role == Claims.Role && c.Fresh(now, host, ProcessInfo.Alive)))
                    return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var log = Path.Combine(Shared.DataDir, Shared.LogName);
            if (!File.Exists(log))
                return false;
            try
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                var lines = new List<string>();
                while (sr.ReadLine() is { } line)
                    lines.Add(line);
                return UpdateRules.StreamingByLog(lines, pid => ProcessInfo.Alive(pid, null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void WaitUntilIdle()
        {
            if (!Streaming())
                return;
            Console.WriteLine("An app is using the webcam. Close it (or stop its video) to update; waiting... (Ctrl+C stops)");
            while (Streaming())
                Thread.Sleep(1000);
            Console.WriteLine("No app uses the webcam now.");
        }

        private static void Remember(string path)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(HkcuKey);
                key.SetValue(UpdateSourceValue, path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Console.WriteLine($"note: cannot remember the update source ({e.Message})");
            }
        }

        private static string? ReadString(RegistryKey root, string key, string name)
        {
            try
            {
                using var k = root.OpenSubKey(key);
                return k?.GetValue(name) as string;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.EnumerateDirectories(from))
                CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }

        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        private static string? Value(string[] args, string flag)
        {
            var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static int Fail(string message, int code)
        {
            Console.Error.WriteLine(message);
            return code;
        }

        // -- msi.dll -------------------------------------------------------------------------------
        private static class Msi
        {
            private const uint ErrorMoreData = 234;
            private const uint ErrorNoMoreItems = 259;

            [DllImport("msi", CharSet = CharSet.Unicode)]
            private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr database);

            [DllImport("msi", CharSet = CharSet.Unicode)]
            private static extern uint MsiDatabaseOpenViewW(IntPtr database, string query, out IntPtr view);

            [DllImport("msi")]
            private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

            [DllImport("msi")]
            private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

            [DllImport("msi", CharSet = CharSet.Unicode)]
            private static extern uint MsiRecordGetStringW(IntPtr record, uint field, StringBuilder? value, ref uint length);

            [DllImport("msi")]
            private static extern uint MsiCloseHandle(IntPtr handle);

            [DllImport("msi", CharSet = CharSet.Unicode)]
            public static extern uint MsiEnumRelatedProductsW(string upgradeCode, uint reserved, uint index, StringBuilder product);

            [DllImport("msi", CharSet = CharSet.Unicode)]
            private static extern uint MsiGetProductInfoW(string product, string property, StringBuilder? value, ref uint length);

            public static string? ProductInfo(string product, string property)
            {
                uint len = 64;
                var sb = new StringBuilder((int)len);
                var r = MsiGetProductInfoW(product, property, sb, ref len);
                if (r == ErrorMoreData)
                {
                    len++;
                    sb = new StringBuilder((int)len);
                    r = MsiGetProductInfoW(product, property, sb, ref len);
                }
                return r == 0 ? sb.ToString() : null;
            }

            /// <summary>The package's Property table (read-only; MSIDBOPEN_READONLY = 0).</summary>
            public static List<(string Name, string Value)> Properties(string path)
            {
                var result = new List<(string, string)>();
                Check(MsiOpenDatabaseW(path, IntPtr.Zero, out var db), path);
                try
                {
                    Check(MsiDatabaseOpenViewW(db, "SELECT `Property`, `Value` FROM `Property`", out var view), path);
                    try
                    {
                        Check(MsiViewExecute(view, IntPtr.Zero), path);
                        while (true)
                        {
                            var r = MsiViewFetch(view, out var rec);
                            if (r == ErrorNoMoreItems)
                                break;
                            Check(r, path);
                            try
                            {
                                result.Add((Field(rec, 1), Field(rec, 2)));
                            }
                            finally
                            {
                                MsiCloseHandle(rec);
                            }
                        }
                    }
                    finally
                    {
                        MsiCloseHandle(view);
                    }
                }
                finally
                {
                    MsiCloseHandle(db);
                }
                return result;
            }

            private static string Field(IntPtr rec, uint field)
            {
                uint len = 256;
                var sb = new StringBuilder((int)len);
                var r = MsiRecordGetStringW(rec, field, sb, ref len);
                if (r == ErrorMoreData)
                {
                    len++;
                    sb = new StringBuilder((int)len);
                    r = MsiRecordGetStringW(rec, field, sb, ref len);
                }
                Check(r, "record");
                return sb.ToString();
            }

            private static void Check(uint r, string what)
            {
                if (r != 0)
                    throw new Win32Exception((int)r, $"{what}: {new Win32Exception((int)r).Message}");
            }
        }

        // -- the Frame Server services -------------------------------------------------------------
        private static class Services
        {
            private const uint ScManagerConnect = 0x0001;
            private const uint ServiceQueryStatus = 0x0004;
            private const uint ServiceStop = 0x0020;
            private const uint ControlStop = 1;
            private const uint StateStopped = 1;
            private const int ErrorServiceNotActive = 1062;
            private const int ErrorServiceDoesNotExist = 1060;

            [StructLayout(LayoutKind.Sequential)]
            private struct ServiceStatus
            {
                public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
            }

            [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

            [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

            [DllImport("advapi32", SetLastError = true)]
            private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

            [DllImport("advapi32", SetLastError = true)]
            private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

            [DllImport("advapi32")]
            private static extern bool CloseServiceHandle(IntPtr handle);

            /// <summary>Stops the service and waits up to 20 s; returns what happened, in words.</summary>
            public static string Stop(string name)
            {
                var scm = OpenSCManagerW(null, null, ScManagerConnect);
                if (scm == IntPtr.Zero)
                    return $"cannot open the service manager ({new Win32Exception(Marshal.GetLastWin32Error()).Message})";
                try
                {
                    var svc = OpenServiceW(scm, name, ServiceQueryStatus | ServiceStop);
                    if (svc == IntPtr.Zero)
                    {
                        var err = Marshal.GetLastWin32Error();
                        return err == ErrorServiceDoesNotExist ? "not on this PC" : $"cannot open ({new Win32Exception(err).Message})";
                    }
                    try
                    {
                        if (QueryServiceStatus(svc, out var st) && st.CurrentState == StateStopped)
                            return "not running";
                        if (!ControlService(svc, ControlStop, out _))
                        {
                            var err = Marshal.GetLastWin32Error();
                            return err == ErrorServiceNotActive ? "not running" : $"not stopped ({new Win32Exception(err).Message})";
                        }
                        var sw = Stopwatch.StartNew();
                        while (sw.Elapsed < TimeSpan.FromSeconds(20))
                        {
                            if (QueryServiceStatus(svc, out st) && st.CurrentState == StateStopped)
                                return "stopped (Windows starts it again when an app opens a camera)";
                            Thread.Sleep(200);
                        }
                        return "still stopping after 20 s (the installer may ask for a restart)";
                    }
                    finally
                    {
                        CloseServiceHandle(svc);
                    }
                }
                finally
                {
                    CloseServiceHandle(scm);
                }
            }
        }
    }
}
