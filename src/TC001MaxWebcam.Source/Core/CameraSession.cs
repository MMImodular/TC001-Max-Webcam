// TC001 Max Thermal Webcam: one TC001 Max streaming session (open, start, read, recover, close).
//
// Origin: a C# port of the stream lifecycle of the author's earlier Python driver (not public).
// The start and stop sequences are adapted from WendyOS PR #2621 protocol.go session.start
// (Apache License 2.0, Copyright Wendy Labs) and jvdillon/p3-ir-camera P3_PROTOCOL.md /
// p3_camera.py (Apache License 2.0, Copyright jvdillon and contributors); the reader loop, the
// stall recovery, the auto-shutter handling and the state machine were written for this project.
// Sources for every transfer are in docs/PROTOCOL.md ([REF]):
//
//  1. open the link (WinUSB on MI_00; interface 0 and, through it, interface 1);
//  2. read the model register, then (best effort) firmware and serial ([REF] "Commands");
//  3. start the stream ([REF] "Start sequence", WendyOS protocol.go session.start, P3_PROTOCOL.md
//     steps 3-8): start_stream, wait 1 s, interface 1 alt 1, vendor request 0xEE (wIndex 1), wait
//     2 s, one throwaway 16 KiB bulk read (a timeout is normal), start_stream again;
//  4. read bulk IN 0x81 in 16 KiB pieces into a BlockParser that drops the first (stale) block.
//  Stop: interface 1 back to alt 0, release both interfaces ([REF] "Stop").
//
// Automatic shutter ([REF] "Automatic shutter", settings auto_shutter / idle_auto_shutter): when
// WantAutoShutter is set, the switch is read after the start and set to the wish (off only when
// asked; on when the camera reports off). On a clean close (the last app stopped streaming, or the
// webcam yields the camera to labbench) IdleAutoShutter says what to leave it at: off (the default,
// like labbench's idle_auto_shutter: no click while nothing streams), on, or untouched. Without an
// IdleAutoShutter a stream that switched it off switches it back on. A failed link sends nothing.
//
// Stalls are not errors ([REF] "Stalls"; despite the state's name they are not the shutter, see there):
// a STALL on bulk IN -> state "calibrating", clear the halt (ClearFeature(ENDPOINT_HALT), USB 2.0
// spec 9.4.5; WinUsb_ResetPipe) and keep reading, dropping the first block after the pause; no
// block 3 s after the stall -> start_stream once more; still nothing after 8 s (or more than 20
// stalls in a row, or the clear fails) -> a full reopen ("reconnecting", retried every 3 s).
// Reference behaviour, verified on the real camera on 2026-11-20 with the Python driver (libusb on
// the same WinUSB driver): the bulk IN 0x81 read failed with EPIPE, clear_halt cleared it in
// 0.05 s and the stream continued, with no reconnect. UNVERIFIED: the same recovery through this
// C# code (ERROR_GEN_FAILURE as the stall, WinUsb_ResetPipe as the clear), the soft restart after
// 3 s and the reopen after 8 s (tested against fakes only), and what causes the stalls.

using System;
using System.Text;
using System.Threading;

namespace TC001MaxWebcam.Core
{
    public class TransportException(string message) : Exception(message);
    public sealed class UsbTimeoutException(string message) : TransportException(message);
    public class UsbErrorException(string message) : TransportException(message);

    /// <summary>The endpoint answered with a STALL; it stays halted until the host clears it.</summary>
    public sealed class UsbStallException(string message) : UsbErrorException(message);

    /// <summary>The camera cannot be opened; <see cref="Advice"/> says what the owner can do.</summary>
    public sealed class CameraUnavailableException(string message, string advice = "", bool busy = false) : Exception(message)
    {
        public string Advice { get; } = advice;
        /// <summary>Another program holds the device (WinUSB allows one at a time).</summary>
        public bool Busy { get; } = busy;
    }

    /// <summary>What the session needs from USB (WinUsbLink in the media source, a fake in the self-test).
    /// Every method except <see cref="Abort"/> and <see cref="Close"/> throws UsbTimeoutException /
    /// UsbErrorException / UsbStallException.</summary>
    public interface IUsbLink
    {
        /// <summary>Find and open the device, claim interfaces 0 and 1 (CameraUnavailableException if impossible).</summary>
        void Open();
        /// <summary>Negotiated speed after Open: low / full / high / unknown.</summary>
        string Speed { get; }
        int ControlOut(byte requestType, byte request, ushort value, ushort index, byte[] data, int timeoutMs);
        byte[] ControlIn(byte requestType, byte request, ushort value, ushort index, int length, int timeoutMs);
        void SetAlt(ushort iface, byte alt);
        int BulkRead(byte endpoint, byte[] buffer, int timeoutMs);
        void ClearHalt(byte endpoint);
        /// <summary>Cancel a blocked read (any thread). Never throws.</summary>
        void Abort();
        /// <summary>Release everything. Idempotent, never throws.</summary>
        void Close();
    }

    public interface IClock
    {
        double Monotonic { get; }
        double Wall { get; }
        /// <summary>Sleeps up to <paramref name="seconds"/>; returns early (false) when <paramref name="stop"/> is set.</summary>
        bool Sleep(double seconds, WaitHandle stop);
    }

    public sealed class RealClock : IClock
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public double Monotonic => _sw.Elapsed.TotalSeconds;
        public double Wall => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        public bool Sleep(double seconds, WaitHandle stop) => !stop.WaitOne(TimeSpan.FromSeconds(Math.Max(seconds, 0)));
    }

    /// <summary>One decoded camera frame (sensor orientation).</summary>
    public sealed class CameraFrame
    {
        public required float[] Temperature { get; init; }
        public required byte[] Image { get; init; }
        public int Sequence { get; init; }
        public double TWall { get; init; }
        public double TMonotonic { get; init; }
    }

    public sealed class CameraSession
    {
        private const int ControlTimeoutMs = 1000;
        private const int BulkTimeoutMs = 1000;
        private const int StatusPolls = 100;          // WendyOS protocol.go: 100 polls 10 ms apart
        private const double StatusPollS = 0.01;
        private const double SettleAfterStartS = 1.0; // [REF] "Start sequence"
        private const double SettleAfterInterfaceS = 2.0;
        private const int StallReads = 3;             // consecutive bulk timeouts = the stream has stopped
        private const double StallPauseS = 0.02;

        private readonly Func<IUsbLink> _linkFactory;
        private readonly IClock _clock;
        private readonly ManualResetEvent _stop = new(false);
        private readonly object _stateLock = new();
        private readonly byte[] _chunk = new byte[Tc001Max.BulkReadBytes];
        private IUsbLink? _link;
        private bool _streaming;
        private int _calStalls;
        private bool _calSoftRestarted;
        private bool? _autoShutterTried;   // the last value we tried to set on this link (no retry loop)
        private volatile bool _opened;     // link.Open succeeded and the link is not closed yet

        public CameraSession(Func<IUsbLink> linkFactory, IClock clock)
        {
            _linkFactory = linkFactory;
            _clock = clock;
            Parser = new BlockParser();
            SetState("starting", "opening the camera");
        }

        // -- options (the original Python driver's defaults) -------------------------------------
        public double SoftRestartAfterS { get; init; } = 3.0;
        public double CalibrationTimeoutS { get; init; } = 8.0;
        public int MaxStallClears { get; init; } = 20;
        public double ReconnectIntervalS { get; init; } = 3.0;
        /// <summary>While the camera is busy before <see cref="FastRetryUntil"/> (monotonic), retry
        /// opening this often instead of every <see cref="ReconnectIntervalS"/>: after a handoff the
        /// other program needs a moment to let go.</summary>
        public double FastRetryIntervalS { get; init; } = 0.5;
        public double FastRetryUntil { get; set; } = double.NegativeInfinity;
        /// <summary>Timeout of each transfer of the idle auto-shutter command at close (labbench: 300 ms).</summary>
        public int CloseControlTimeoutMs { get; init; } = 300;
        public bool SkipAfterCalibration { get; init; } = true;

        /// <summary>Whether the camera's automatic shutter should run (settings auto_shutter; asked on
        /// every read, so a settings change applies within a moment). null = never touch it (the
        /// behaviour before 2026-11-20). See SyncAutoShutter.</summary>
        public Func<bool>? WantAutoShutter { get; set; }

        /// <summary>What to leave the automatic shutter at on a clean close (settings idle_auto_shutter):
        /// false = off, true = on, null = as it is. When this property itself is null: switch it back on
        /// if this stream switched it off (the behaviour before idle_auto_shutter).</summary>
        public Func<bool?>? IdleAutoShutter { get; set; }

        // -- state ---------------------------------------------------------------------------------
        public BlockParser Parser { get; private set; }
        /// <summary>starting | streaming | calibrating | reconnecting | stopped.</summary>
        public string State { get; private set; } = "starting";
        public double StateSince { get; private set; }
        public string Detail { get; private set; } = "";
        public string LastError { get; private set; } = "";
        public string Advice { get; private set; } = "";
        public bool LastErrorBusy { get; private set; }
        public string Model { get; private set; } = "";
        public string Firmware { get; private set; } = "";
        public string Serial { get; private set; } = "";
        public string Speed { get; private set; } = "";
        public int Reconnects { get; private set; }
        public int Calibrations { get; private set; }
        public int StallClears { get; private set; }
        public int SoftRestarts { get; private set; }
        public int RejectedFrames { get; private set; }
        /// <summary>Frames whose picture leak was repaired (Tc001Max.RepairPictureLeak).</summary>
        public int RepairedFrames { get; private set; }
        public long FramesDelivered { get; private set; }
        /// <summary>The camera's automatic shutter as last read or set (null = unknown).</summary>
        public bool? AutoShutter { get; private set; }

        /// <summary>Called on the reader thread for every valid frame.</summary>
        public Action<CameraFrame>? OnFrame { get; set; }

        /// <summary>True while the device is open (WinUSB handle held).</summary>
        public bool DeviceOpen => _opened;

        public WaitHandle StopHandle => _stop;

        private void SetState(string state, string detail = "")
        {
            lock (_stateLock)
            {
                if (state != State)
                {
                    State = state;
                    StateSince = _clock.Monotonic;
                }
                Detail = detail;
            }
        }

        /// <summary>Ask <see cref="Run"/> to end (any thread): cancels a blocked read at once.</summary>
        public void RequestStop()
        {
            _stop.Set();
            try
            {
                _link?.Abort();
            }
            catch
            {
                // Abort never throws by contract; a fake might
            }
        }

        public bool StopRequested => _stop.WaitOne(0);

        /// <summary>Streams until <see cref="RequestStop"/>: reconnects on errors; always closes the device.</summary>
        public void Run()
        {
            var timeouts = 0;
            var first = true;
            try
            {
                while (!StopRequested)
                {
                    try
                    {
                        if (_link == null)
                        {
                            StartStream();
                            if (!first)
                                Reconnects++;
                            first = false;
                            if (StopRequested)
                                break;
                        }
                        try
                        {
                            Step();
                            timeouts = 0;
                        }
                        catch (UsbTimeoutException)
                        {
                            timeouts++;
                            if (timeouts < StallReads)
                                continue;
                            throw new TransportException("the stream stopped (bulk reads time out)");
                        }
                        continue;
                    }
                    catch (CameraUnavailableException e)
                    {
                        Fail(e.Message, e.Advice, e.Busy);
                    }
                    catch (Exception e)
                    {
                        if (StopRequested)
                            break;
                        Fail(e.Message, "", false);
                    }
                    timeouts = 0;
                    _clock.Sleep(LastErrorBusy && _clock.Monotonic < FastRetryUntil ? FastRetryIntervalS : ReconnectIntervalS, _stop);
                }
            }
            finally
            {
                StopStream(clean: true);
                SetState("stopped", "closed");
            }
        }

        private void Fail(string message, string advice, bool busy)
        {
            if (message != LastError)
                Log.Warn($"camera error, reconnecting: {message}");
            LastError = message;
            Advice = advice;
            LastErrorBusy = busy;
            SetState("reconnecting", message);
            StopStream(clean: false);
        }

        // -- protocol ------------------------------------------------------------------------------
        /// <summary>[REF] "Commands": write, poll the busy bit, read the response, read the status again.
        /// <paramref name="timeoutMs"/> and <paramref name="polls"/> are shorter at close.</summary>
        private byte[] SendCommand(IUsbLink link, byte[] cmd, int responseLength, int timeoutMs = ControlTimeoutMs, int polls = StatusPolls)
        {
            link.ControlOut(Tc001Max.ReqTypeVendorOutInterface, Tc001Max.ReqCommand, 0, Tc001Max.ControlInterface, cmd, timeoutMs);
            for (var poll = 0; poll <= polls; poll++)
            {
                var st = link.ControlIn(Tc001Max.ReqTypeVendorInInterface, Tc001Max.ReqStatus, 0, Tc001Max.ControlInterface, 1, timeoutMs);
                if (st.Length == 0 || (st[0] & 1) == 0)
                    break;
                if (poll == polls)
                    throw new TransportException($"camera stayed busy (status 0x{st[0]:x2})");
                // at close the stop event is already set and returns at once: wait anyway
                if (!_clock.Sleep(StatusPollS, _stop))
                    Thread.Sleep(TimeSpan.FromSeconds(StatusPollS));
            }
            var resp = Array.Empty<byte>();
            if (responseLength > 0)
                resp = link.ControlIn(Tc001Max.ReqTypeVendorInInterface, Tc001Max.ReqResponse, 0, Tc001Max.ControlInterface, responseLength, timeoutMs);
            link.ControlIn(Tc001Max.ReqTypeVendorInInterface, Tc001Max.ReqStatus, 0, Tc001Max.ControlInterface, 1, timeoutMs);
            return resp;
        }

        private static string Text(byte[] resp)
        {
            var n = Array.IndexOf(resp, (byte)0);
            return Encoding.ASCII.GetString(resp, 0, n < 0 ? resp.Length : n).Trim();
        }

        private void StartStream()
        {
            SetState(State == "starting" ? "starting" : "reconnecting", "opening the camera");
            var link = _linkFactory();
            _link = link;
            try
            {
                link.Open();
                _opened = true;
                Speed = link.Speed;
                if (Speed is "low" or "full")
                    Log.Warn($"the camera is on a USB {Speed}-speed link: it cannot carry the ~35 MB/s stream");
                Model = Text(SendCommand(link, Tc001Max.CmdReadModel, 30));
                if (Model.Length > 0 && !Model.Contains("TC001"))
                    Log.Warn($"camera reports model '{Model}', expected 'TC001 Max'");
                try
                {
                    Firmware = Text(SendCommand(link, Tc001Max.CmdReadFirmware, 12));
                }
                catch (TransportException e)
                {
                    Log.Info($"firmware register not readable: {e.Message}");
                }
                try
                {
                    Serial = Text(SendCommand(link, Tc001Max.CmdReadSerial, 64));
                }
                catch (TransportException e)
                {
                    Log.Info($"serial register not readable: {e.Message}");
                }
                // [REF] "Start sequence" (WendyOS protocol.go session.start; P3_PROTOCOL.md steps 3-8)
                SendCommand(link, Tc001Max.CmdStartStream, 1);
                if (!_clock.Sleep(SettleAfterStartS, _stop))
                    return;
                link.SetAlt(Tc001Max.StreamInterface, Tc001Max.StreamAltOn);
                _streaming = true;
                link.ControlOut(Tc001Max.ReqTypeVendorOutDevice, Tc001Max.ReqStream, 0, Tc001Max.StreamInterface, [], ControlTimeoutMs);
                if (!_clock.Sleep(SettleAfterInterfaceS, _stop))
                    return;
                try
                {
                    link.BulkRead(Tc001Max.StreamEndpoint, _chunk, BulkTimeoutMs);
                }
                catch (UsbTimeoutException)
                {
                    // normal: the priming read often returns nothing
                }
                catch (UsbStallException)
                {
                    ClearHalt(link); // a shutter during the start sequence
                }
                SendCommand(link, Tc001Max.CmdStartStream, 1);
                // The first block after a (re)start was buffered by the camera before the stream was
                // (re)started: on the owner's camera it was ~2 s old (frame counter jump of 53).
                Parser = new BlockParser(skipFirst: true);
                LastError = "";
                Advice = "";
                LastErrorBusy = false;
                SetState("streaming");
                Log.Info($"streaming: {Model} firmware {Firmware}, USB {Speed}");
                SyncAutoShutter(link, startup: true);
            }
            catch
            {
                StopStream(clean: StopRequested);
                throw;
            }
        }

        /// <summary>Closes the link. <paramref name="clean"/>: a requested stop (the link still works),
        /// so the idle auto-shutter policy is applied first; after a failure nothing is sent.</summary>
        private void StopStream(bool clean)
        {
            var link = _link;
            _link = null;
            if (link == null)
                return;
            if (IdleAutoShutter != null)
            {
                if (clean && _opened)
                    ApplyIdleAutoShutter(link, IdleAutoShutter());
            }
            else if (AutoShutter == false)
            {
                // [REF] "Automatic shutter": the switch survives a close / reopen, so give the camera
                // back with its default (automatic shutter on) for the next program.
                try
                {
                    SendCommand(link, Tc001Max.CmdAutoShutterOn, 0);
                    AutoShutter = true;
                    Log.Info("auto shutter switched back on (stream stopped)");
                }
                catch (Exception e)
                {
                    Log.Warn($"could not switch the auto shutter back on: {e.Message} (unplug and replug the camera to restore it)");
                }
            }
            _autoShutterTried = null;
            if (_streaming)
            {
                try
                {
                    link.SetAlt(Tc001Max.StreamInterface, Tc001Max.StreamAltIdle);
                }
                catch (Exception e)
                {
                    Log.Info($"stopping the stream failed: {e.Message}");
                }
            }
            _streaming = false;
            _opened = false;
            link.Close();
        }

        /// <summary>[REF] "Automatic shutter": leave the switch at <paramref name="idle"/> (null = as it is)
        /// before the stream stops. Short timeouts; never throws.</summary>
        private void ApplyIdleAutoShutter(IUsbLink link, bool? idle)
        {
            if (idle is not { } want || AutoShutter == want)
                return;
            try
            {
                SendCommand(link, want ? Tc001Max.CmdAutoShutterOn : Tc001Max.CmdAutoShutterOff, 0, CloseControlTimeoutMs, polls: 20);
                AutoShutter = want;
                Log.Info($"auto shutter {(want ? "on" : "off")} while idle (idle_auto_shutter)");
            }
            catch (Exception e)
            {
                Log.Warn($"could not switch the auto shutter {(want ? "on" : "off")} for idle: {e.Message}");
            }
        }

        /// <summary>
        /// [REF] "Automatic shutter": brings the camera's auto-shutter switch in line with
        /// <see cref="WantAutoShutter"/>. At startup the switch is read first (it may have been left off
        /// by a program that did not restore it). "on" is only sent when the camera reported "off";
        /// "off" is sent whenever asked. Failures are logged and never stop the stream; a failed value
        /// is not retried until the wish changes or the camera is reopened.
        /// </summary>
        private void SyncAutoShutter(IUsbLink link, bool startup)
        {
            if (WantAutoShutter == null)
                return;
            var want = WantAutoShutter();
            if (startup)
            {
                try
                {
                    var r = SendCommand(link, Tc001Max.CmdGetAutoShutter, 1);
                    AutoShutter = r.Length > 0 ? r[0] != 0 : null;
                }
                catch (TransportException e)
                {
                    AutoShutter = null;
                    Log.Info($"auto shutter state not readable: {e.Message}");
                }
                if (AutoShutter == false && want)
                {
                    // labbench and this webcam leave it off while idle (idle_auto_shutter): normal
                    if (IdleAutoShutter != null)
                        Log.Info("the camera's auto shutter was off (idle): switching it on");
                    else
                        Log.Warn("the camera's auto shutter was off (left so by an earlier session): switching it on");
                }
            }
            if (AutoShutter == want || (AutoShutter == null && want) || _autoShutterTried == want)
                return;
            _autoShutterTried = want;
            try
            {
                SendCommand(link, want ? Tc001Max.CmdAutoShutterOn : Tc001Max.CmdAutoShutterOff, 0);
                var r = SendCommand(link, Tc001Max.CmdGetAutoShutter, 1);
                AutoShutter = r.Length > 0 ? r[0] != 0 : want;
                if (AutoShutter == want)
                    Log.Info($"auto shutter {(want ? "on" : "off")}");
                else
                    Log.Warn($"auto shutter: asked for {(want ? "on" : "off")}, the camera reports {(AutoShutter == true ? "on" : "off")}");
            }
            catch (TransportException e)
            {
                Log.Warn($"could not switch the auto shutter {(want ? "on" : "off")}: {e.Message}");
            }
        }

        private void ClearHalt(IUsbLink link)
        {
            try
            {
                link.ClearHalt(Tc001Max.StreamEndpoint);
            }
            catch (UsbErrorException e)
            {
                throw new TransportException($"clearing the stalled stream endpoint failed: {e.Message}");
            }
            StallClears++;
        }

        // -- reading -------------------------------------------------------------------------------
        private int Pump()
        {
            var link = _link ?? throw new TransportException("camera link is closed");
            var n = link.BulkRead(Tc001Max.StreamEndpoint, _chunk, BulkTimeoutMs);
            var blocks = Parser.Feed(_chunk.AsSpan(0, n));
            var delivered = 0;
            foreach (var block in blocks)
            {
                DecodedBlock d;
                try
                {
                    d = Tc001Max.DecodeBlock(block);
                }
                catch (FrameRejectedException)
                {
                    RejectedFrames++;
                    continue;
                }
                if (d.LeakRepaired > 0)
                {
                    RepairedFrames++;
                    // the first few per session, then every 100th: about one every 25 minutes is normal
                    if (RepairedFrames <= 3 || RepairedFrames % 100 == 0)
                        Log.Info($"repaired a frame: {d.LeakRepaired} temperature words held picture data (~240 degC; {RepairedFrames} so far)");
                }
                delivered++;
                FramesDelivered++;
                OnFrame?.Invoke(new CameraFrame
                {
                    Temperature = d.TemperatureC,
                    Image = d.Image,
                    Sequence = d.Sequence,
                    TWall = _clock.Wall,
                    TMonotonic = _clock.Monotonic,
                });
            }
            return blocks.Count;
        }

        private void RestartParser(bool skipFirst)
        {
            var stats = Parser.Stats;
            stats.LastSequence = null; // a counter jump across a pause is not a loss
            Parser = new BlockParser(skipFirst, stats);
        }

        private void OnStall(UsbStallException e)
        {
            var link = _link ?? throw new TransportException("camera link is closed");
            if (State != "calibrating")
            {
                SetState("calibrating", "stream paused (USB stall)");
                _calStalls = 0;
                _calSoftRestarted = false;
                Log.Info($"stream stalled ({e.Message}): clearing it and resuming (not the shutter, docs/PROTOCOL.md \"Stalls\")");
            }
            _calStalls++;
            if (_calStalls > MaxStallClears)
                throw new TransportException($"the stream stayed stalled after {MaxStallClears} clears ({e.Message})");
            ClearHalt(link);
            RestartParser(SkipAfterCalibration);
            _clock.Sleep(StallPauseS, _stop);
            CheckCalibration();
        }

        private void CheckCalibration()
        {
            var elapsed = _clock.Monotonic - StateSince;
            if (elapsed >= CalibrationTimeoutS)
                throw new TransportException($"the stream did not resume {elapsed:F0} s after a stall");
            if (elapsed >= SoftRestartAfterS && !_calSoftRestarted)
            {
                _calSoftRestarted = true;
                var link = _link ?? throw new TransportException("camera link is closed");
                Log.Info($"no frame {elapsed:F1} s after the stall: sending start_stream again");
                // [REF] "Commands": start_stream (verified); P3_PROTOCOL.md: reply 0x35 means the camera
                // was streaming and restarts.
                SendCommand(link, Tc001Max.CmdStartStream, 1);
                SoftRestarts++;
                RestartParser(true);
            }
        }

        private void CalibrationDone()
        {
            var dur = _clock.Monotonic - StateSince;
            Calibrations++;
            Log.Info($"stream resumed after {dur:F1} s (calibration {Calibrations}; {_calStalls} stall(s) cleared{(_calSoftRestarted ? ", soft restart" : "")})");
            SetState("streaming");
        }

        /// <summary>One read with stall handling. Returns new blocks; throws UsbTimeoutException (outside a
        /// calibration) or TransportException (a full reopen is needed).</summary>
        private int Step()
        {
            if (WantAutoShutter != null && State == "streaming" && _link is { } link)
                SyncAutoShutter(link, startup: false);
            int n;
            try
            {
                n = Pump();
            }
            catch (UsbStallException e)
            {
                OnStall(e);
                return 0;
            }
            catch (UsbTimeoutException)
            {
                if (State != "calibrating")
                    throw;
                CheckCalibration();
                return 0;
            }
            if (State == "calibrating")
            {
                if (n > 0)
                    CalibrationDone();
                else
                    CheckCalibration();
            }
            return n;
        }
    }
}
