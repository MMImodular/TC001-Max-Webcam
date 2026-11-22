// TC001 Max Thermal Webcam: taking turns with labbench (C:\Git\Automated_Testing) on the TC001 Max.
//
// WinUSB lets one process at a time open the camera. labbench's processes agree on who may hold it
// with claim files (labbench.thermal.ownership, src/labbench/thermal/ownership.py): every process
// that wants or holds the camera keeps claim-<role>-<pid>.json fresh (rewritten about once a second,
// deleted when it lets go); a process may open the camera only while no fresh claim of a HIGHER
// priority exists, and a holder that sees one closes the camera within about a second (it yields).
//
//   role       priority  who
//   run_host   30        a labbench run's thermal safety monitor
//   gui        20        the labbench GUI's Thermal tab (Start camera)
//   webcam     10        this virtual camera, while an app streams from it
//
// The webcam is the lowest: labbench always gets the camera when it asks, and the webcam takes it
// back by itself when labbench lets go. The claims live in a folder both sides can reach:
// C:\ProgramData\TC001MaxWebcam\claims (created by the installer; LOCAL SERVICE, the Frame Server's
// account, cannot read the per-user ~\.labbench\thermal). labbench uses it when it exists.
//
// Claim file (UTF-8 JSON, written to <name>.tmp and swapped in; readers share read/write/delete and
// skip files they cannot parse):
//   {"version": 1, "role": "webcam", "priority": 10, "pid": 8556, "host": "GOOB",
//    "created": 1795214644.10, "t_wall": 1795214923.03, "expires_s": 5.0,
//    "state": "wanted" | "holding", "source": "TC001 Max Thermal Webcam"}
// A claim is fresh while now - t_wall <= expires_s and, for a claim from this computer, its process
// is alive (a process this account may not query counts as alive: the Frame Server cannot query a
// user's python.exe, so a crashed labbench blocks the webcam for at most expires_s).

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TC001MaxWebcam.Core
{
    public sealed record Claim(string Role, int Priority, int Pid, string Host, double? Created, double TWall,
                               double ExpiresS, string State, string Source, string Path)
    {
        /// <summary>Heartbeat recent and (same computer) the process alive (ownership.Claim.fresh).</summary>
        public bool Fresh(double nowWall, string myHost, Func<int, double?, bool> alive)
        {
            if (nowWall - TWall > ExpiresS)
                return false;
            if (Host.Length > 0 && !string.Equals(Host, myHost, StringComparison.OrdinalIgnoreCase))
                return true;
            return alive(Pid, Created);
        }

        /// <summary>Owner-facing text, e.g. "run 2026... (run host) (pid 8556)" (ownership.Claim.describe).</summary>
        public string Describe() => $"{(Source.Length > 0 ? Source : Role)} (pid {Pid})";
    }

    public static class Claims
    {
        public const string Role = "webcam";
        public const int Priority = 10;
        public const double ExpiresS = 5.0;
        public const double HeartbeatS = 1.0;
        /// <summary>Largest claim file read (they are about 250 bytes; anything bigger is not a claim).</summary>
        public const int MaxBytes = 16 * 1024;

        public static readonly IReadOnlyDictionary<string, int> Priorities =
            new Dictionary<string, int> { ["run_host"] = 30, ["gui"] = 20, [Role] = Priority };

        /// <summary>One claim file's text, or null if it is not a claim (ownership.read_claims).</summary>
        public static Claim? Parse(string json, string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object)
                    return null;
                var role = Str(r, "role") ?? "";
                var priority = r.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number
                    ? (int)p.GetDouble() : Priorities.GetValueOrDefault(role, 0);
                if (!r.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number)
                    return null;
                if (!r.TryGetProperty("t_wall", out var tw) || tw.ValueKind != JsonValueKind.Number)
                    return null;
                double? created = r.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
                var expires = r.TryGetProperty("expires_s", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : ExpiresS;
                return new Claim(role, priority, (int)pid.GetDouble(), Str(r, "host") ?? "", created, tw.GetDouble(), expires,
                                 Str(r, "state") ?? "wanted", Str(r, "source") ?? "", path);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
            {
                return null;
            }
        }

        private static string? Str(JsonElement r, string name) =>
            r.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

        /// <summary>Every parseable claim file in <paramref name="dir"/>, deduplicated by role + pid. Never throws.</summary>
        public static List<Claim> Read(string dir)
        {
            var seen = new Dictionary<(string, int), Claim>();
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "claim-*.json");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return [];
            }
            foreach (var f in files)
            {
                string text;
                try
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length > MaxBytes)
                        continue;
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    text = sr.ReadToEnd();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue; // being replaced: the next poll reads it
                }
                var c = Parse(text, f);
                if (c == null)
                    continue;
                var key = (c.Role, c.Pid);
                if (!seen.TryGetValue(key, out var old) || c.TWall > old.TWall)
                    seen[key] = c;
            }
            return [.. seen.Values];
        }

        /// <summary>The highest-priority fresh claim above the webcam's, from another process, or null.</summary>
        public static Claim? Higher(IEnumerable<Claim> claims, int myPid, string myHost, double nowWall,
                                    Func<int, double?, bool> alive)
        {
            Claim? best = null;
            foreach (var c in claims)
            {
                if (c.Pid == myPid && string.Equals(c.Host, myHost, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (c.Priority <= Priority || !c.Fresh(nowWall, myHost, alive))
                    continue;
                if (best == null || c.Priority > best.Priority)
                    best = c;
            }
            return best;
        }

        /// <summary>The JSON the webcam writes (the format labbench's CameraArbiter.heartbeat writes).</summary>
        public static string Format(int pid, string host, double? created, double tWall, bool holding, string source)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteString("role", Role);
                w.WriteNumber("priority", Priority);
                w.WriteNumber("pid", pid);
                w.WriteString("host", host);
                if (created is { } cr)
                    w.WriteNumber("created", cr);
                else
                    w.WriteNull("created");
                w.WriteNumber("t_wall", tWall);
                w.WriteNumber("expires_s", ExpiresS);
                w.WriteString("state", holding ? "holding" : "wanted");
                w.WriteString("source", source);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public static string FileName(int pid) => $"claim-{Role}-{pid}.json";

        /// <summary>Writes the claim atomically (temporary file + replace). Throws IOException /
        /// UnauthorizedAccessException; the caller logs once and carries on.</summary>
        public static void Write(string dir, string json, int pid)
        {
            var target = System.IO.Path.Combine(dir, FileName(pid));
            var tmp = target + ".tmp";
            File.WriteAllBytes(tmp, Encoding.UTF8.GetBytes(json));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, target, overwrite: true);
                    return;
                }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 3)
                {
                    Thread.Sleep(5); // a reader holds it open without delete sharing
                }
            }
        }

        /// <summary>Removes the webcam's claim (and a left-over temporary file). Never throws.</summary>
        public static void Delete(string dir, int pid)
        {
            foreach (var name in new[] { FileName(pid), FileName(pid) + ".tmp" })
            {
                try
                {
                    File.Delete(System.IO.Path.Combine(dir, name));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                }
            }
        }
    }

    /// <summary>Same rules as labbench.core.process.process_alive (Windows): a process this account may
    /// not query counts as alive; a creation time that differs by more than 1 s means a reused pid.</summary>
    public static class ProcessInfo
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ErrorAccessDenied = 5;
        private const uint StillActive = 259;
        private const double FiletimeEpochOffsetS = 11_644_473_600.0;

        public static bool Alive(int pid, double? created)
        {
            if (pid <= 0)
                return false;
            var (exists, actual) = Query(pid);
            if (!exists)
                return false;
            if (created is null || actual is null)
                return true;
            return Math.Abs(actual.Value - created.Value) <= 1.0;
        }

        /// <summary>Creation time of a running process (epoch seconds), or null.</summary>
        public static double? Created(int pid) => Query(pid).Created;

        private static (bool Exists, double? Created) Query(int pid)
        {
            var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (h == IntPtr.Zero)
                return (Marshal.GetLastWin32Error() == ErrorAccessDenied, null);
            try
            {
                if (GetExitCodeProcess(h, out var code) && code != StillActive)
                    return (false, null);
                if (!GetProcessTimes(h, out var c, out _, out _, out _))
                    return (true, null);
                return (true, c / 1e7 - FiletimeEpochOffsetS);
            }
            finally
            {
                CloseHandle(h);
            }
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr h, out uint code);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32")]
        private static extern bool CloseHandle(IntPtr h);
    }
}
