// TC001 Max Thermal Webcam tray: the update check and the one-click update.
//
// At most once a day while the update_check setting is on (and whenever you pick "Check for updates"),
// a background thread asks GitHub for this repository's latest release (Core\ReleaseCheck.cs has the
// rules). If it is newer than this program, the settings window shows an Update button and a Windows
// notification says so, once per version. The button downloads the installer to
// %LOCALAPPDATA%\TC001MaxWebcam\updates\, checks its size and the SHA-256 that GitHub lists for it, and
// hands it to "TC001MaxWebcam.exe update" (Updater.cs): that waits until no app uses the webcam, asks
// for administrator rights once and upgrades in place (settings stay).
//
// What is remembered between runs (%LOCALAPPDATA%\TC001MaxWebcam\update-check.json): when it last
// asked, whether that failed, and the last version it notified about. Nothing is sent but the
// request itself (a User-Agent naming this program and its version).
// Origin: written for this project (2026-11-23).

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TC001MaxWebcam.Core;

namespace TC001MaxWebcam.Tray
{
    internal enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Starting, Failed }

    /// <summary>What the settings window shows.</summary>
    internal sealed record UpdateView(UpdateState State, string Text, ReleaseInfo? Release, int Percent);

    internal sealed class Updates
    {
        private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TC001MaxWebcam");
        private static readonly string StatePath = Path.Combine(Dir, "update-check.json");
        private static readonly string DownloadDir = Path.Combine(Dir, "updates");

        public static readonly Version Current =
            UpdateRules.ParseVersion(typeof(Updates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0])
            ?? UpdateRules.ParseVersion(typeof(Updates).Assembly.GetName().Version?.ToString()) ?? new Version(0, 0, 0);

        private readonly Action _changed;                 // called on a worker thread: post to the window
        private readonly object _lock = new();
        private UpdateView _view = new(UpdateState.Idle, "", null, 0);
        private DateTime? _lastUtc;
        private bool _lastFailed;
        private string? _notified;
        private int _busy;

        /// <summary>Set when a check finds a version not notified before: the tray shows a notification and clears it.</summary>
        public ReleaseInfo? Notify { get; set; }

        public Updates(Action changed)
        {
            _changed = changed;
            LoadState();
        }

        public UpdateView View { get { lock (_lock) return _view; } }

        public bool Busy => Volatile.Read(ref _busy) != 0;

        /// <summary>Starts a check if one is due (or <paramref name="manual"/>). Never blocks.</summary>
        public void MaybeCheck(bool enabled, bool manual)
        {
            if (!manual && (!enabled || !ReleaseCheck.Due(_lastUtc, _lastFailed, DateTime.UtcNow)))
                return;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return;
            SetView(UpdateState.Checking, "Checking...", View.Release, 0);
            Task.Run(async () =>
            {
                try { await Check(manual); }
                finally { Volatile.Write(ref _busy, 0); _changed(); }
            });
        }

        private async Task Check(bool manual)
        {
            try
            {
                using var http = Client(TimeSpan.FromSeconds(20));
                using var resp = await http.GetAsync(ReleaseCheck.LatestUrl);
                _lastUtc = DateTime.UtcNow;
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _lastFailed = false;
                    SetView(UpdateState.UpToDate, $"Up to date ({ReleaseCheck.Format(Current)})", null, 0);
                    return;
                }
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}");
                var json = await resp.Content.ReadAsStringAsync();
                _lastFailed = false;
                var release = ReleaseCheck.Parse(json, out var why);
                if (release == null)
                {
                    SetView(UpdateState.UpToDate, $"Up to date ({ReleaseCheck.Format(Current)})", null, 0);
                    Log.Info("update check: " + why);
                    return;
                }
                if (!UpdateRules.IsNewer(release.Version, Current))
                {
                    SetView(UpdateState.UpToDate, $"Up to date ({ReleaseCheck.Format(Current)})", null, 0);
                    return;
                }
                SetView(UpdateState.Available, $"{ReleaseCheck.Format(release.Version)} available (you have {ReleaseCheck.Format(Current)})", release, 0);
                if (manual || ReleaseCheck.ShouldNotify(release, _notified))
                {
                    _notified = ReleaseCheck.Format(release.Version);
                    Notify = release;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                _lastUtc = DateTime.UtcNow;
                _lastFailed = true;
                SetView(UpdateState.Failed, "Check failed (offline?)", View.Release, 0);
            }
            finally
            {
                SaveState();
            }
        }

        /// <summary>Downloads <paramref name="r"/>, checks it, and starts "TC001MaxWebcam update" with it.</summary>
        public void Install(ReleaseInfo r)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return;
            SetView(UpdateState.Downloading, $"Downloading {ReleaseCheck.Format(r.Version)}...", r, 0);
            Task.Run(async () =>
            {
                try { await DownloadAndStart(r); }
                finally { Volatile.Write(ref _busy, 0); _changed(); }
            });
        }

        private async Task DownloadAndStart(ReleaseInfo r)
        {
            var final = Path.Combine(DownloadDir, r.AssetName);
            var part = final + ".part";
            try
            {
                Directory.CreateDirectory(DownloadDir);
                foreach (var old in Directory.EnumerateFiles(DownloadDir))   // earlier downloads
                    if (!string.Equals(old, final, StringComparison.OrdinalIgnoreCase))
                        TryDelete(old);
                if (!(File.Exists(final) && new FileInfo(final).Length == r.Size && ReleaseCheck.HashMatches(r.Sha256, HashOf(final))))
                {
                    using var http = Client(TimeSpan.FromMinutes(10));
                    using var resp = await http.GetAsync(r.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    using var sha = SHA256.Create();
                    long got = 0;
                    await using (var src = await resp.Content.ReadAsStreamAsync())
                    await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buf = new byte[81920];
                        int n;
                        var lastPct = -1;
                        while ((n = await src.ReadAsync(buf)) > 0)
                        {
                            got += n;
                            if (got > r.Size)
                                throw new IOException($"the download is larger than the {r.Size:N0} bytes GitHub lists");
                            sha.TransformBlock(buf, 0, n, null, 0);
                            await dst.WriteAsync(buf.AsMemory(0, n));
                            var pct = (int)(got * 100 / r.Size);
                            if (pct != lastPct && pct % 5 == 0)
                            {
                                lastPct = pct;
                                SetView(UpdateState.Downloading, $"Downloading {ReleaseCheck.Format(r.Version)}... {pct} %", r, pct);
                            }
                        }
                    }
                    sha.TransformFinalBlock([], 0, 0);
                    if (got != r.Size)
                        throw new IOException($"the download has {got:N0} bytes, GitHub lists {r.Size:N0}");
                    if (!ReleaseCheck.HashMatches(r.Sha256, sha.Hash!))
                        throw new IOException("the download does not match the SHA-256 GitHub lists; it was deleted");
                    File.Move(part, final, overwrite: true);
                }
                Log.Info($"update: downloaded and checked {final} (sha256 {r.Sha256})");

                var exe = Path.Combine(AppContext.BaseDirectory, "TC001MaxWebcam.exe");
                if (!File.Exists(exe))
                    throw new IOException($"{exe} is missing; install the .msi by hand: {final}");
                var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = DownloadDir };
                foreach (var a in new[] { "update", "--from", final, "--no-remember", "--pause" })
                    psi.ArgumentList.Add(a);
                using var _ = Process.Start(psi);
                SetView(UpdateState.Starting, $"Installing {ReleaseCheck.Format(r.Version)}: see the update window", r, 100);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                TryDelete(part);
                if (e is IOException && e.Message.Contains("SHA-256", StringComparison.Ordinal))
                    TryDelete(final);
                Log.Info("update: download failed: " + e.Message);
                SetView(UpdateState.Failed, "Download failed (details in tray.log)", r, 0);
            }
        }

        private static HttpClient Client(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"TC001MaxWebcam-Tray/{ReleaseCheck.Format(Current)}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            return http;
        }

        private static byte[] HashOf(string path)
        {
            using var f = File.OpenRead(path);
            return SHA256.HashData(f);
        }

        private void SetView(UpdateState s, string text, ReleaseInfo? r, int pct)
        {
            lock (_lock)
                _view = new UpdateView(s, text, r, pct);
            _changed();
        }

        private void LoadState()
        {
            try
            {
                if (!File.Exists(StatePath))
                    return;
                using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
                var r = doc.RootElement;
                if (r.TryGetProperty("last_check_utc", out var t) && t.TryGetDateTime(out var dt))
                    _lastUtc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                _lastFailed = r.TryGetProperty("last_failed", out var f) && f.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("notified_version", out var n) && n.ValueKind == JsonValueKind.String)
                    _notified = n.GetString();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }

        private void SaveState()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(new { last_check_utc = _lastUtc, last_failed = _lastFailed, notified_version = _notified });
                File.WriteAllText(StatePath, json);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
