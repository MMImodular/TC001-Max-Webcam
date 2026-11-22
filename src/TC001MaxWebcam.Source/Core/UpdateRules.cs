// TC001 Max Thermal Webcam: the decisions of "TC001MaxWebcam update" (pure code; the self-test checks
// them). The Windows parts (reading .msi files, elevation, the Frame Server, msiexec) are in
// src\TC001MaxWebcam\Updater.cs.
//
// Where updates come from: a folder of .msi files (normally the top of the repository, where build.ps1
// puts the installer). Only packages
// with this product's UpgradeCode count, the newest ProductVersion wins, and it is installed only when
// it is newer than the installed version (Windows Installer compares the first three fields).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TC001MaxWebcam.Core
{
    /// <summary>One .msi file as read from its Property table (null = not readable / not set).</summary>
    public sealed record MsiInfo(string Path, string? UpgradeCode, string? ProductVersion);

    public static class UpdateRules
    {
        /// <summary>installer/Package.wxs Package/@UpgradeCode: the same for every version of this product.</summary>
        public const string UpgradeCode = "{811A58FA-9195-44F7-8D75-1161873DB4A1}";

        /// <summary>The installer's file names (installer/TC001MaxWebcam.Installer.wixproj OutputName).</summary>
        public const string MsiPattern = "TC001MaxWebcam-*.msi";

        /// <summary>"1.2.0" -> 1.2.0; Windows Installer versions have three significant fields, so a
        /// fourth is dropped and a missing third is 0. Null if it is not a version.</summary>
        public static Version? ParseVersion(string? text)
        {
            if (!Version.TryParse(text?.Trim(), out var v))
                return null;
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }

        /// <summary>The newest package of this product among <paramref name="msis"/>, or null.</summary>
        public static (MsiInfo Msi, Version Version)? Newest(IEnumerable<MsiInfo> msis)
        {
            (MsiInfo, Version)? best = null;
            foreach (var m in msis.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(m.UpgradeCode?.Trim(), UpgradeCode, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ParseVersion(m.ProductVersion) is not { } v)
                    continue;
                if (best is not { } b || v > b.Item2)
                    best = (m, v);
            }
            return best;
        }

        /// <summary>Whether <paramref name="candidate"/> would upgrade <paramref name="installed"/>
        /// (null = not installed: anything is an upgrade).</summary>
        public static bool IsNewer(Version candidate, Version? installed) => installed == null || candidate > installed;

        /// <summary>
        /// Where to look for updates, first match wins: the --from argument (a folder or one .msi), the
        /// folder remembered from an earlier --from, then the folder the installed .msi was run from
        /// (the installer records it). A package Windows Installer ran from its own cache
        /// (%WINDIR%\Installer, a repair) says nothing about where new builds appear and is skipped.
        /// Returns the path and a few words on where it came from, or null.
        /// </summary>
        public static (string Path, string Origin)? Source(string? fromArg, string? remembered, string? installedFrom, string windowsDir)
        {
            if (!string.IsNullOrWhiteSpace(fromArg))
                return (fromArg.Trim(), "--from");
            if (!string.IsNullOrWhiteSpace(remembered))
                return (remembered.Trim(), "remembered from an earlier --from");
            if (!string.IsNullOrWhiteSpace(installedFrom))
            {
                var dir = System.IO.Path.GetDirectoryName(installedFrom.Trim());
                var cache = System.IO.Path.Combine(windowsDir, "Installer");
                if (!string.IsNullOrEmpty(dir) && !IsUnder(dir, cache))
                    return (dir, "the folder the installed version was installed from");
            }
            return null;
        }

        private static bool IsUnder(string path, string folder)
        {
            var p = System.IO.Path.TrimEndingDirectorySeparator(path);
            var f = System.IO.Path.TrimEndingDirectorySeparator(folder);
            return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(f + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether an app is streaming from the webcam, judged from webcam.log (FrameSource.cs writes
        /// "&lt;stamp&gt; [&lt;pid&gt;] info  stream started: ..." when the first app starts and "... last
        /// stream stopped: ..." when the last one stops): true when the newest of those two lines is a
        /// start and the Frame Server process that wrote it is still alive (a crashed service writes no
        /// stop line).
        /// </summary>
        public static bool StreamingByLog(IEnumerable<string> lines, Func<int, bool> alive)
        {
            int? startedPid = null;
            foreach (var line in lines)
            {
                if (line.Contains(StartedText, StringComparison.Ordinal))
                    startedPid = LinePid(line) ?? -1;
                else if (line.Contains(StoppedText, StringComparison.Ordinal))
                    startedPid = null;
            }
            return startedPid is { } pid && (pid < 0 || alive(pid));
        }

        public const string StartedText = "stream started:";
        public const string StoppedText = "last stream stopped:";

        private static int? LinePid(string line)
        {
            var a = line.IndexOf('[');
            var b = a < 0 ? -1 : line.IndexOf(']', a + 1);
            return b > a + 1 && int.TryParse(line.AsSpan(a + 1, b - a - 1), out var pid) ? pid : null;
        }

        /// <summary>What a msiexec exit code means here (msiexec /i ... /passive /norestart).</summary>
        public static (bool Installed, string Text) DescribeMsiExit(int code) => code switch
        {
            0 => (true, "installed"),
            3010 => (true, "installed; Windows asks for a restart to finish (some files were in use)"),
            1641 => (true, "installed; Windows is restarting"),
            1602 => (false, "cancelled; nothing changed"),
            1618 => (false, "another installation is running; try again when it has finished"),
            1638 => (false, "this version is already installed"),
            _ => (false, $"failed (msiexec exit code {code})"),
        };

        /// <summary>The .msi files to consider at <paramref name="source"/> (a folder, or one file).</summary>
        public static IEnumerable<string> MsiFiles(string source)
        {
            if (File.Exists(source))
                return [source];
            if (Directory.Exists(source))
                return Directory.EnumerateFiles(source, MsiPattern, SearchOption.TopDirectoryOnly);
            return [];
        }
    }
}
