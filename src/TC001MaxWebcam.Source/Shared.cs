// TC001 Max Thermal Webcam: identifiers shared by the media source and TC001MaxWebcam.exe.
// The layout of the class follows VCamNetSample's Shared.cs (Copyright (c) 2024-2026 Simon Mourier,
// MIT License, see THIRD_PARTY_NOTICES.txt); the values are this project's own.

using System;
using System.IO;

namespace TC001MaxWebcam
{
    public static class Shared
    {
        /// <summary>CLSID of the media source activator (our own GUID, NOT the sample's).</summary>
        public const string CLSID_TC001MaxWebcam = "c81b40c5-3cf7-4733-b7cc-a000f86da438";

        /// <summary>Windows appends " Windows Virtual Camera" to this name (MFCreateVirtualCamera docs).</summary>
        public const string FriendlyName = "TC001 Max Thermal Webcam";

        /// <summary>ETW provider for traces (string-only events), see README "Troubleshooting".</summary>
        public const string EtwProviderId = "04d5d965-c194-4077-83b7-93a67ae288e0";

        /// <summary>Output size and rate (the TC001 Max streams about 25 frames per second).</summary>
        public const uint Width = 640;
        public const uint Height = 480;
        public const uint FramesPerSecond = 25;

        /// <summary>C:\ProgramData\TC001MaxWebcam: settings.json (written by the owner) and the media
        /// source's log (written by the Frame Server, LOCAL SERVICE). The installer sets its ACL.</summary>
        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TC001MaxWebcam");

        /// <summary>C:\ProgramData\TC001MaxWebcam\claims: camera claims shared with labbench
        /// (Core/CameraClaims.cs). Every local account may add files and change only its own.</summary>
        public static string ClaimDir => Path.Combine(DataDir, ClaimDirName);
        public const string ClaimDirName = "claims";

        public const string SettingsName = "settings.json";
        public static string SettingsPath => Path.Combine(DataDir, SettingsName);

        /// <summary>The media source's own log (rotated at LogMaxBytes to webcam.log.1).</summary>
        public const string LogName = "webcam.log";
        public const long LogMaxBytes = 256 * 1024;

        /// <summary>The notification-area (tray) app, TC001MaxWebcamTray.exe, next to TC001MaxWebcam.exe.
        /// One per sign-in session (the mutex); setting the event asks it to exit (the updater does so
        /// before msiexec replaces its files, then starts it again).</summary>
        public const string TrayExeName = "TC001MaxWebcamTray.exe";
        public const string TrayMutexName = @"Local\TC001MaxWebcamTray";
        public const string TrayExitEventName = @"Local\TC001MaxWebcamTray.Exit";
    }
}
