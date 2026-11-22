// TC001 Max Thermal Webcam: owns the TC001 Max while an app streams from the virtual camera.
//
// While at least one stream runs (Acquire / Release), a CameraSession reads the camera over WinUSB
// on its own thread; when the last stream stops, the session is asked to stop (the blocked read is
// aborted, interface 1 goes back to alt 0 and every handle is freed, within about a second). If
// another program holds the USB device (WinUSB lets one program open it at a time), the picture is
// a "camera in use by another program" card and the session retries every 3 s. settings.json is
// read when a stream starts and re-read when it changes (SettingsWatcher).
//
// Taking turns with labbench (CameraClaims.cs; labbench.thermal.ownership): with a ClaimDir, a
// supervisor thread polls the claim folder every 0.25 s while a stream runs.
//  * A fresh labbench claim (a run host or the GUI's Thermal tab; both rank above the webcam) ->
//    LEND: stop the reader (the camera is closed within about a second, leaving the automatic
//    shutter at idle_auto_shutter) and show a "lent to labbench" card. The webcam's own claim says
//    "holding" until the device is closed, then it is deleted.
//  * No such claim -> OWN: read the camera and keep claim-webcam-<pid>.json fresh ("wanted" while
//    opening, "holding" while the device is open), so labbench can tell who has the camera. After
//    labbench lets go, a busy camera is retried every 0.5 s for 5 s (labbench is still closing it).
// A missing or unwritable claim folder never stops the picture: the webcam then behaves as before
// (labbench's open fails while the webcam streams; labbench retries until the app stops).

using System;
using System.IO;
using System.Threading;

namespace TC001MaxWebcam.Core
{
    public sealed class EngineOptions
    {
        public required Func<IUsbLink> LinkFactory { get; init; }
        public IClock Clock { get; init; } = new RealClock();
        /// <summary>settings.json; null = the defaults only.</summary>
        public string? SettingsPath { get; init; }
        public double SettingsPollS { get; init; } = 1.0;
        /// <summary>A frame older than this is not shown while reconnecting.</summary>
        public double KeepLastFrameS { get; init; } = 30.0;
        /// <summary>Builds each CameraSession (tests shorten the waits).</summary>
        public Func<Func<IUsbLink>, IClock, CameraSession>? SessionFactory { get; init; }

        // -- taking turns with labbench (CameraClaims.cs) ------------------------------------------
        /// <summary>The shared claim folder (C:\ProgramData\TC001MaxWebcam\claims); null = no claims.</summary>
        public string? ClaimDir { get; init; }
        public Func<int, double?, bool> Alive { get; init; } = ProcessInfo.Alive;
        public string Host { get; init; } = System.Net.Dns.GetHostName();
        public int Pid { get; init; } = Environment.ProcessId;
        public double? Created { get; init; } = ProcessInfo.Created(Environment.ProcessId);
        public string ClaimSource { get; init; } = "TC001 Max Thermal Webcam";
        /// <summary>How often the claim folder is read (real time).</summary>
        public double ClaimPollS { get; init; } = 0.25;
        /// <summary>Supervisor period (real time).</summary>
        public double TickS { get; init; } = 0.1;
        /// <summary>Fast busy retries for this long after labbench let go.</summary>
        public double FastRetryS { get; init; } = 5.0;
    }

    public sealed class WebcamEngine : IDisposable
    {
        public const string Source = "TC001 Max";
        public const string BusyTitle = "Camera in use by another program";
        public const string NoCameraTitle = "No thermal camera";
        public const string LentTitle = "Camera lent to labbench";

        private readonly EngineOptions _o;
        private readonly object _lock = new();      // picture versioning
        private readonly object _runLock = new();   // Start / Stop / supervisor decisions
        private readonly AutoResetEvent _wake = new(false);
        private volatile bool _active;
        private volatile bool _disposed;
        private volatile CameraSession? _session;
        private CameraSession? _closing;            // the previous session while its thread still runs
        private Thread? _sessionThread;
        private Thread? _supervisor;
        private volatile CameraFrame? _frame;

        // claims
        private volatile Claim? _holder;
        private volatile bool _lent;
        private readonly System.Diagnostics.Stopwatch _claimPollWatch = new();   // real time, like TickS
        private double _nextHeartbeat = double.NegativeInfinity;
        private double _fastRetryUntil = double.NegativeInfinity;
        private bool _claimWritten;
        private bool? _claimHolding;
        private bool _claimWriteFailed;

        // picture versioning
        private object? _lastKeyRef;
        private string _lastKeyText = "";
        private long _version;

        public WebcamEngine(EngineOptions options)
        {
            _o = options;
            Settings = new SettingsWatcher(options.SettingsPath, options.Clock, options.SettingsPollS);
        }

        public SettingsWatcher Settings { get; }
        public bool Active => _active;
        public CameraSession? Session => _session;
        /// <summary>True while a reader thread (current or stopping) still runs.</summary>
        public bool ReaderRunning => _sessionThread is { IsAlive: true };
        /// <summary>The labbench claim the camera is lent to, or null.</summary>
        public Claim? LentTo => _lent ? _holder : null;
        /// <summary>This process's claim file (null without a claim folder).</summary>
        public string? ClaimPath => _o.ClaimDir == null ? null : Path.Combine(_o.ClaimDir, Claims.FileName(_o.Pid));

        // -- process-wide instance -----------------------------------------------------------------
        private static readonly object SharedLock = new();
        private static WebcamEngine? _shared;
        private static int _refs;

        /// <summary>Starts (or keeps running) the process-wide engine; pair with <see cref="Release"/>.</summary>
        public static WebcamEngine Acquire(Func<EngineOptions> options)
        {
            lock (SharedLock)
            {
                _shared ??= new WebcamEngine(options());
                _refs++;
                _shared.Start();
                return _shared;
            }
        }

        /// <summary>One stream fewer; the camera is closed when none is left.</summary>
        public static void Release()
        {
            lock (SharedLock)
            {
                if (_refs == 0 || _shared == null)
                    return;
                if (--_refs == 0)
                    _shared.Stop();
            }
        }

        // -- running -------------------------------------------------------------------------------
        /// <summary>Reads the settings and starts reading the camera unless labbench has it (idempotent).</summary>
        public void Start()
        {
            lock (_runLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _active = true;
                Settings.Poll(force: true);
                _claimPollWatch.Reset(); // decide on fresh claims now
                Tick();
                if (_o.ClaimDir != null && _supervisor is not { IsAlive: true })
                {
                    _supervisor = new Thread(Loop) { IsBackground = true, Name = "TC001 Max webcam supervisor" };
                    _supervisor.Start();
                }
            }
            _wake.Set();
        }

        /// <summary>Stops reading (asynchronously; never blocks the caller).</summary>
        public void Stop()
        {
            lock (_runLock)
            {
                if (!_active)
                    return;
                _active = false;
                _frame = null;
                _session?.RequestStop();
                Log.Info("last stream stopped: closing the camera");
            }
            _wake.Set();
        }

        private void Loop()
        {
            while (!_disposed)
            {
                try
                {
                    lock (_runLock)
                    {
                        if (_active)
                        {
                            Tick();
                        }
                        else if (ReaderRunning)
                        {
                            Heartbeat(holding: Holding());   // still closing: say so until it is closed
                        }
                        else
                        {
                            DeleteClaim();
                            _supervisor = null;
                            return;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"webcam supervisor: {e}");
                }
                _wake.WaitOne(TimeSpan.FromSeconds(_o.TickS));
            }
        }

        /// <summary>One decision (under _runLock): lend the camera to labbench, or read it ourselves.</summary>
        private void Tick()
        {
            if (!_active)
                return;
            var mono = _o.Clock.Monotonic;
            if (_o.ClaimDir != null && (!_claimPollWatch.IsRunning || _claimPollWatch.Elapsed.TotalSeconds >= _o.ClaimPollS))
            {
                _claimPollWatch.Restart();
                _holder = Claims.Higher(Claims.Read(_o.ClaimDir), _o.Pid, _o.Host, _o.Clock.Wall, _o.Alive);
            }
            if (_sessionThread is { IsAlive: false })
                _closing = null;

            var holder = _holder;
            if (holder != null)
            {
                if (!_lent)
                {
                    _lent = true;
                    Log.Info($"lending the TC001 Max to {holder.Describe()} (labbench has priority)");
                }
                if (_session is { StopRequested: false } s)
                {
                    s.RequestStop();
                    _frame = null;
                }
                if (ReaderRunning)
                    Heartbeat(holding: Holding());
                else
                    DeleteClaim();
                return;
            }

            if (_lent)
            {
                _lent = false;
                _fastRetryUntil = mono + _o.FastRetryS;
                Log.Info("labbench let go of the TC001 Max: taking it back");
            }
            if (_session is not { StopRequested: false })
                StartReader();
            Heartbeat(holding: Holding());
        }

        private bool Holding() => (_session?.DeviceOpen ?? false) || (_closing?.DeviceOpen ?? false);

        /// <summary>A new reader; it waits for a previous one that is still closing (never two on the device).</summary>
        private void StartReader()
        {
            var previous = _sessionThread;
            _closing = previous is { IsAlive: true } ? _session : null;
            var session = _o.SessionFactory?.Invoke(_o.LinkFactory, _o.Clock) ?? new CameraSession(_o.LinkFactory, _o.Clock);
            session.OnFrame = f =>
            {
                if (ReferenceEquals(_session, session))
                    _frame = f;
            };
            // settings auto_shutter / idle_auto_shutter (docs/PROTOCOL.md "Automatic shutter"); applied by the reader
            var watcher = Settings;
            session.WantAutoShutter = () => watcher.Current.AutoShutter;
            session.IdleAutoShutter = () => watcher.Current.IdleAutoShutter;
            session.FastRetryUntil = _fastRetryUntil;
            _frame = null;
            _session = session;
            _sessionThread = new Thread(() =>
            {
                previous?.Join();
                session.Run();
            }) { IsBackground = true, Name = "TC001 Max reader" };
            _sessionThread.Start();
            Log.Info("stream started: opening the TC001 Max");
        }

        private void Heartbeat(bool holding)
        {
            if (_o.ClaimDir == null)
                return;
            var mono = _o.Clock.Monotonic;
            if (mono < _nextHeartbeat && holding == _claimHolding)
                return;
            _nextHeartbeat = mono + Claims.HeartbeatS;
            try
            {
                // never created here: the installer creates the folder with the ACL both sides need
                if (!Directory.Exists(_o.ClaimDir))
                    throw new DirectoryNotFoundException($"{_o.ClaimDir} does not exist");
                Claims.Write(_o.ClaimDir, Claims.Format(_o.Pid, _o.Host, _o.Created, _o.Clock.Wall, holding, _o.ClaimSource), _o.Pid);
                _claimWritten = true;
                _claimHolding = holding;
                if (_claimWriteFailed)
                    Log.Info("claim file written again");
                _claimWriteFailed = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (!_claimWriteFailed)
                    Log.Warn($"cannot write the camera claim {ClaimPath} ({e.Message}); labbench can still ask for the camera, " +
                             "but cannot tell who has it (the installer creates the folder with this right)");
                _claimWriteFailed = true;
            }
        }

        private void DeleteClaim()
        {
            if (!_claimWritten || _o.ClaimDir == null)
                return;
            Claims.Delete(_o.ClaimDir, _o.Pid);
            _claimWritten = false;
            _claimHolding = null;
            _nextHeartbeat = double.NegativeInfinity;
        }

        // -- what to show --------------------------------------------------------------------------
        /// <summary>The picture to show now (any thread; the media source calls it for every frame).</summary>
        public Picture Current()
        {
            Settings.Poll();
            if (_active && LentTo is { } holder)
            {
                return Card(LentTitle,
                    $"labbench is using the TC001 Max: {holder.Describe()}. It has priority over this webcam. " +
                    "The picture comes back by itself when it lets go.");
            }
            var s = _session;
            if (!_active || s == null)
                return Card(FrameRenderer.Footer, "Starting...");
            var frame = _frame;
            var state = s.State;
            if (frame != null && _o.Clock.Monotonic - frame.TMonotonic > _o.KeepLastFrameS)
                frame = null;
            if (state == "reconnecting" && s.LastErrorBusy)
            {
                return Card(BusyTitle,
                    "Another program has the TC001 Max open (WinUSB lets one program use it at a time). " +
                    "Close it - for example the TOPDON app, a script, or another program that reads the camera " +
                    "over USB - and the picture appears here. Retrying every 3 s.");
            }
            if (frame == null)
            {
                if (state == "reconnecting")
                {
                    var text = s.LastError.Length > 0 ? s.LastError : "The TC001 Max is not answering.";
                    if (s.Advice.Length > 0)
                        text += " " + s.Advice;
                    return Card(NoCameraTitle, text + " Retrying every 3 s.");
                }
                return Card("Starting the camera", "Opening the TC001 Max over USB. The first picture takes about 4 s.");
            }
            string? badge = state switch
            {
                "calibrating" => "CALIBRATING (SHUTTER)",
                "reconnecting" => "RECONNECTING",
                "starting" => "RECONNECTING",
                _ => null,
            };
            var settings = Settings.Current;
            var settingsVersion = Settings.Version;
            return new Picture
            {
                Kind = PictureKind.Live,
                Version = VersionFor(frame, $"{badge}|{settingsVersion}"),
                Temperature = frame.Temperature,
                Width = Tc001Max.Width,
                Height = Tc001Max.Height,
                Settings = settings,
                SettingsVersion = settingsVersion,
                TWall = frame.TWall,
                Source = Source,
                Badge = badge,
                BadgeCalm = state == "calibrating",
                Dim = badge != null,
            };
        }

        private Picture Card(string title, string text) =>
            Picture.Card(VersionFor(null, title + "\n" + text), title, text);

        private long VersionFor(object? content, string text)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(content, _lastKeyRef) || text != _lastKeyText)
                {
                    _lastKeyRef = content;
                    _lastKeyText = text;
                    _version++;
                }
                return _version;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _active = false;
            _session?.RequestStop();
            _wake.Set();
            _sessionThread?.Join(TimeSpan.FromSeconds(3));
            _supervisor?.Join(TimeSpan.FromSeconds(1));
            lock (_runLock)
                DeleteClaim();
        }
    }
}
