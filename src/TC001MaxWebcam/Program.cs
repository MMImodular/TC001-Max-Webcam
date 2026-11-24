// TC001MaxWebcam.exe: creates, removes or temporarily runs the TC001 Max Thermal Webcam, shows its
// status and edits its settings.
//
// The MFCreateVirtualCamera / IMFVirtualCamera calls follow VCamNetSample's Main.cs
// (https://github.com/smourier/VCamNetSample, Copyright (c) 2024-2026 Simon Mourier, MIT License,
// see THIRD_PARTY_NOTICES.txt). The console commands and the system-lifetime handling are this
// project's own.
//
//   install [--current-user]  (elevated) create the camera with MFVirtualCameraLifetime_System so it
//                             stays listed across log-offs and reboots. Default access: all users
//                             (MFVirtualCameraAccess_AllUsers needs admin).
//   uninstall                 (elevated) remove the camera (both access scopes are tried). It does not
//                             unregister the media source DLL or delete files: the installer (or uninstall.ps1) does.
//   run                       fallback without System lifetime: a session-lifetime camera for the
//                             current user that exists while this command runs (Ctrl+C ends it).
//   status                    read-only: COM registration, elevation, the data folder, the settings,
//                             the end of webcam.log and whether Windows lists the TC001 Max with
//                             WinUSB (the device is never opened here).
//   settings                  print the current settings (and any problems in settings.json).
//   settings set k=v [k=v..]  validate and write settings.json (nothing is written if a value is bad).
//   settings reset            write the defaults.
//   update [--from PATH] [--no-remember] [--check] [--now]
//                             install a newer .msi of this product from the update source (one UAC
//                             prompt; Updater.cs).
//
// The media source DLL must be registered first (regsvr32 TC001MaxWebcamSource.comhost.dll, HKLM
// only: the .msi writes the same keys, see installer/Package.wxs and install.ps1).

using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using DirectN;
using TC001MaxWebcam.Core;
using TC001MaxWebcam.Usb;
using Microsoft.Win32;

namespace TC001MaxWebcam.Cli
{
    internal static class Program
    {
        private static readonly string Usage = string.Join(Environment.NewLine, UsageLines());

        private static string[] UsageLines() =>
        [
            "usage: TC001MaxWebcam <command>",
            "  install [--current-user]   (elevated) create the virtual camera (system lifetime)",
            "  uninstall                  (elevated) remove the virtual camera",
            "  run                        a camera for this session only, until Ctrl+C",
            "  status                     installation, settings, log and USB device (read-only)",
            "  settings                   print the current settings",
            "  settings set key=value ... change settings (validated), for example:",
            "                               TC001MaxWebcam settings set rotate=180 units=F palette=iron",
            "  settings reset             back to the defaults",
            "  update [--from PATH]       install the newest TC001MaxWebcam-*.msi from PATH (remembered), or from",
            "                             the folder the installed one came from (one administrator prompt)",
            "  update --check             only say whether a newer version is there",
            "  update --now               do not wait for apps to stop streaming from the webcam",
            "  update --no-remember       with --from: do not keep PATH as the update source",
            "                             (the tray icon's Update button installs GitHub releases this way)",
            "settings keys:",
            .. SettingsFile.Help.Select(h => "  " + h),
        ];

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8; // the degree sign
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                Console.Error.WriteLine("Virtual cameras need Windows 11 (build 22000 or later).");
                return 2;
            }

            var command = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            var currentUser = Array.Exists(args, a => a.Equals("--current-user", StringComparison.OrdinalIgnoreCase));
            try
            {
                return command switch
                {
                    "install" => Install(currentUser),
                    "uninstall" => Uninstall(),
                    "run" => Run(),
                    "status" => Status(),
                    "settings" => SettingsCommand(args.Skip(1).ToArray()),
                    "update" => Updater.Run(args.Skip(1).ToArray()),
                    "help" or "-h" or "--help" or "/?" => Help(),
                    _ => Fail(Usage, 2),
                };
            }
            catch (Exception e)
            {
                return Fail("error: " + e.Message, 1);
            }
        }

        private static int Help()
        {
            Console.WriteLine(Usage);
            return 0;
        }

        private static int Fail(string message, int code)
        {
            Console.Error.WriteLine(message);
            return code;
        }

        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static string Describe(HRESULT hr) => $"0x{(uint)hr.Value:X8} {new Win32Exception(hr.Value).Message}";

        // -- the virtual camera --------------------------------------------------------------------
        private static HRESULT Create(__MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002 lifetime,
                                      __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003 access,
                                      out IMFVirtualCamera? camera)
        {
            var hr = Functions.MFCreateVirtualCamera(
                __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0001.MFVirtualCameraType_SoftwareCameraSource,
                lifetime, access, Shared.FriendlyName, "{" + Shared.CLSID_TC001MaxWebcam + "}", null, 0, out var created);
            camera = hr.IsSuccess ? created : null;
            return hr;
        }

        private static int Install(bool currentUser)
        {
            if (!IsElevated())
                return Fail("install must run elevated (Run as administrator): the camera is created for all users with system lifetime.", 3);

            var access = currentUser
                ? __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_CurrentUser
                : __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_AllUsers;
            Console.WriteLine($"MFCreateVirtualCamera: \"{Shared.FriendlyName}\", source {{{Shared.CLSID_TC001MaxWebcam}}}, lifetime System, access {(currentUser ? "CurrentUser" : "AllUsers")}");
            MFFunctions.MFStartup();
            try
            {
                var hr = Create(__MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002.MFVirtualCameraLifetime_System, access, out var camera);
                if (hr.IsError || camera == null)
                    return Fail("MFCreateVirtualCamera failed: " + Describe(hr), 1);

                using var cam = new ComObject<IMFVirtualCamera>(camera);
                hr = cam.Object.Start(null);
                if (hr.IsError)
                {
                    return Fail("IMFVirtualCamera.Start failed: " + Describe(hr) +
                                "\n  Access denied usually means the Frame Server services cannot read the media source DLL" +
                                " (it must be in C:\\Program Files\\TC001MaxWebcam, registered: repair the install, or run install.ps1).", 1);
                }

                Console.WriteLine($"Virtual camera created. Apps list it as \"{Shared.FriendlyName} Windows Virtual Camera\".");
                // Shutdown releases our handle; for a System-lifetime camera it does not remove the
                // camera (IMFVirtualCamera::Shutdown docs: only Session-lifetime cameras are removed).
                cam.Object.Shutdown();
                return 0;
            }
            finally
            {
                MFFunctions.MFShutdown();
            }
        }

        private static int Uninstall()
        {
            if (!IsElevated())
                return Fail("uninstall must run elevated (Run as administrator).", 3);

            MFFunctions.MFStartup();
            try
            {
                var removed = 0;
                foreach (var access in new[]
                         {
                             __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_AllUsers,
                             __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_CurrentUser,
                         })
                {
                    var hr = Create(__MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002.MFVirtualCameraLifetime_System, access, out var camera);
                    if (hr.IsError || camera == null)
                    {
                        Console.WriteLine($"  {access}: cannot open ({Describe(hr)})");
                        continue;
                    }

                    using var cam = new ComObject<IMFVirtualCamera>(camera);
                    hr = cam.Object.Remove();
                    if (hr.IsSuccess)
                    {
                        removed++;
                        Console.WriteLine($"  {access}: virtual camera removed");
                    }
                    else
                    {
                        Console.WriteLine($"  {access}: nothing removed ({Describe(hr)})");
                    }
                    cam.Object.Shutdown();
                }
                Console.WriteLine(removed > 0 ? "Done." : "No TC001 Max Thermal Webcam was registered.");
                return 0;
            }
            finally
            {
                MFFunctions.MFShutdown();
            }
        }

        private static int Run()
        {
            MFFunctions.MFStartup();
            try
            {
                var hr = Create(__MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002.MFVirtualCameraLifetime_Session,
                                __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_CurrentUser, out var camera);
                if (hr.IsError || camera == null)
                    return Fail("MFCreateVirtualCamera failed: " + Describe(hr), 1);

                using var cam = new ComObject<IMFVirtualCamera>(camera);
                hr = cam.Object.Start(null);
                if (hr.IsError)
                    return Fail("IMFVirtualCamera.Start failed: " + Describe(hr) + " (is the media source registered? repair the install, or run install.ps1)", 1);

                Console.WriteLine($"\"{Shared.FriendlyName}\" runs for this session. Press Ctrl+C to remove it.");
                using var done = new ManualResetEventSlim();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    done.Set();
                };
                done.Wait();
                cam.Object.Remove();
                cam.Object.Shutdown();
                Console.WriteLine("Removed.");
                return 0;
            }
            finally
            {
                MFFunctions.MFShutdown();
            }
        }

        // -- status --------------------------------------------------------------------------------
        private static int Status()
        {
            Console.WriteLine($"camera name : {Shared.FriendlyName} (listed with \" Windows Virtual Camera\" appended)");
            Console.WriteLine($"elevated    : {IsElevated()}");

            var key = $@"SOFTWARE\Classes\CLSID\{{{Shared.CLSID_TC001MaxWebcam}}}\InprocServer32";
            using (var reg = Registry.LocalMachine.OpenSubKey(key))
            {
                var dll = reg?.GetValue(null) as string;
                Console.WriteLine(dll == null
                    ? "media source: NOT registered (repair the install in Settings > Apps, or run install.ps1 elevated)"
                    : $"media source: {dll}{(File.Exists(dll) ? "" : "  (FILE MISSING)")}");
            }

            var dir = Shared.DataDir;
            Console.WriteLine(Directory.Exists(dir) ? $"data folder : {dir}" : $"data folder : {dir} does not exist (the installer creates it)");
            StatusClaims();
            StatusSettings();
            StatusUpdate();
            StatusLog();
            StatusDevice();
            return 0;
        }

        /// <summary>The camera claims shared with labbench (Core/CameraClaims.cs): who has or wants the camera.</summary>
        private static void StatusClaims()
        {
            var dir = Shared.ClaimDir;
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"claims      : {dir} does not exist (the installer creates it): labbench cannot ask a streaming webcam for the camera");
                return;
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var host = System.Net.Dns.GetHostName();
            var fresh = Claims.Read(dir).Where(c => c.Fresh(now, host, ProcessInfo.Alive)).OrderByDescending(c => c.Priority).ToList();
            Console.WriteLine($"claims      : {dir} ({(fresh.Count == 0 ? "nobody has or wants the camera" : $"{fresh.Count} fresh")})");
            foreach (var c in fresh)
                Console.WriteLine($"  {c.Role,-9} : {c.State} - {c.Describe()} (priority {c.Priority})");
        }

        private static void StatusUpdate()
        {
            try
            {
                Console.WriteLine($"version     : {Updater.InstalledVersion()?.ToString() ?? "not installed by the .msi"}; {Updater.StatusLine()}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"version     : cannot check for updates ({e.Message})");
            }
        }

        private static void StatusSettings()
        {
            var path = Shared.SettingsPath;
            try
            {
                var problems = new List<string>();
                var s = SettingsFile.Load(path, problems);
                Console.WriteLine($"settings    : {SettingsWatcher.Describe(s)}{(File.Exists(path) ? "" : " (defaults: no settings.json yet)")}");
                foreach (var p in problems)
                    Console.WriteLine($"  problem   : {p}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"settings    : cannot read {path} ({e.Message})");
            }
        }

        private static void StatusLog()
        {
            var log = Path.Combine(Shared.DataDir, Shared.LogName);
            if (!File.Exists(log))
            {
                Console.WriteLine("log         : none yet (the camera writes it while an app streams from it)");
                return;
            }
            Console.WriteLine($"log         : {log} (last lines)");
            try
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                var lines = new Queue<string>();
                while (sr.ReadLine() is { } line)
                {
                    lines.Enqueue(line);
                    if (lines.Count > 8)
                        lines.Dequeue();
                }
                foreach (var line in lines)
                    Console.WriteLine("  " + line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"  cannot read it ({e.Message})");
            }
        }

        /// <summary>Whether Windows lists the camera with WinUSB (SetupAPI only: the device is NOT opened).</summary>
        private static void StatusDevice()
        {
            try
            {
                var devices = UsbDiscovery.Find();
                if (devices.Count == 0)
                    Console.WriteLine($"TC001 Max   : not present ({UsbDiscovery.InstancePrefix}*)");
                foreach (var dev in devices)
                {
                    Console.WriteLine($"TC001 Max   : {dev.InstanceId}");
                    Console.WriteLine(dev.Path != null
                        ? $"              WinUSB interface {dev.InterfaceGuid} (not opened)"
                        : "              no WinUSB device interface: install WinUSB on 'TC001 Max (Interface 0)' with Zadig");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"TC001 Max   : cannot list USB devices ({e.Message})");
            }
        }

        // -- settings ------------------------------------------------------------------------------
        private static int SettingsCommand(string[] args)
        {
            var path = Shared.SettingsPath;
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "show";
            switch (sub)
            {
                case "show":
                {
                    var problems = new List<string>();
                    var s = SettingsFile.Load(path, problems);
                    Console.WriteLine(File.Exists(path) ? $"{path}:" : $"{path} does not exist yet; the defaults:");
                    Console.Write(SettingsFile.ToJson(s));
                    foreach (var p in problems)
                        Console.WriteLine($"problem: {p}");
                    return problems.Count == 0 ? 0 : 1;
                }
                case "set":
                case "reset":
                {
                    var problems = new List<string>();
                    var current = SettingsFile.Load(path, problems);
                    WebcamSettings next;
                    if (sub == "reset")
                    {
                        next = WebcamSettings.Default;
                    }
                    else
                    {
                        try
                        {
                            next = SettingsFile.Apply(current, args.Skip(1));
                        }
                        catch (SettingsException e)
                        {
                            Console.Error.WriteLine("Nothing written:");
                            foreach (var line in e.Message.Split('\n'))
                                Console.Error.WriteLine("  " + line);
                            Console.Error.WriteLine("Keys:");
                            foreach (var h in SettingsFile.Help)
                                Console.Error.WriteLine("  " + h);
                            return 2;
                        }
                        foreach (var p in problems)
                            Console.WriteLine($"note: the old file had a problem, now replaced: {p}");
                    }
                    if (!Directory.Exists(Shared.DataDir))
                        return Fail($"{Shared.DataDir} does not exist: install first (the .msi, or install.ps1; it creates the folder with the right permissions).", 1);
                    try
                    {
                        SettingsFile.Save(path, next);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        return Fail($"cannot write {path}: {e.Message}\n  the installer gives the user who ran it (install.ps1: -Owner) Modify on {Shared.DataDir}.", 1);
                    }
                    Console.WriteLine($"Written {path} (a running camera picks it up within about a second):");
                    Console.Write(SettingsFile.ToJson(next));
                    return 0;
                }
                default:
                    return Fail(Usage, 2);
            }
        }
    }
}
