// TC001 Max Thermal Webcam: optional noise reduction on the temperature frames (setting denoise).
//
// Origin: own code, written by Claude (Anthropic) on 2026-11-23, ported from this repository's
// prototype experiments/denoise/denoise.py and changed in one point (below). No code was copied.
// Methods: a motion-adaptive recursive temporal filter, the standard video noise reduction
// (E. Dubois, S. Sabri, "Noise reduction in image sequences using motion-compensated temporal
// filtering", IEEE Trans. Communications 32(7), 1984; J. C. Brailean et al., "Noise reduction
// filters for dynamic image sequences: a review", Proc. IEEE 83(9), 1995), here without motion
// compensation. The whole-frame flicker estimate and the averaging of the camera's shutter offset
// tables through the jumps they cause were worked out from this project's noise study
// (docs/NOISE_AND_CALIBRATION.md and its references; experiments/noise). Shutter clicks are found
// from the repeated frames described in experiments/LOG.md. Measured on recordings with this code:
// still-pixel noise about 31 -> 7 mK, fixed-pattern error about 39 -> 28 mK, motion kept.
//
// Per frame (degC in, a new degC array out; the camera's frame is never changed):
//  1. Shutter tables. Each shutter writes a new per-pixel offset table with ~30 mK of fresh random
//     error; the jump a shutter causes in a still scene is (new table - old table) in its fine part.
//     The sum of the fine jumps is each table relative to the first; subtracting each table's
//     difference from the mean of the last 4 averages the errors. A shutter shows as the camera
//     repeating its last frame (~0.6 s); the jump is measured from a 1-s average before it and the
//     first second after it. A jump that does not look like a shutter's (the scene moved) clears
//     the history, which only switches this stage off until the next shutters.
//  2. Flicker. The whole frame moves together by ~15-18 mK per frame. Its estimate is the mean of
//     (frame - previous output), each pixel clipped at +-3 sigma so moving objects barely pull it.
//     Only its fast part (minus a 1-s running mean) is removed. (The prototype removed all of it,
//     which also held back slow real changes of the whole scene.)
//  3. Recursive filter: y += alpha * (x - y), alpha = 1/16 where the 3x3 mean of |x - y| looks like
//     noise, rising to 1 where it is clearly motion (above 3 sigma).
// Frames more than 2 s apart (a stall, a reconnect) restart the filter and the table history.

using System;
using System.Collections.Generic;

namespace TC001MaxWebcam.Core
{
    public sealed class Denoiser
    {
        public const float SigmaC = 0.032f;        // per-pixel temporal noise per frame (plate, 2026-11-23)
        public const float ClipSigma = 3f;
        public const float AlphaMin = 1f / 16f;    // still pixels: time constant ~16 frames (0.64 s)
        public const float T0Sigma = 1.2f;         // 3x3 mean |x - y| below this = still
        public const float T1Sigma = 3f;           // ... above this = moving (alpha = 1)
        public const float FlickerSlowFrames = 25f;
        public const int RepeatsForShutter = 5;    // identical frames in a row (the shutter repeats ~15)
        public const int JumpFrames = 25;          // 1 s each side of a shutter
        public const int Tables = 4;
        public const int FineRadius = 15;          // the fine part: minus a double 31-px box blur
        public const float JumpFineStdMaxC = 0.09f;  // a shutter's fine jump is ~0.045 rms
        public const float JumpFineAbsMaxC = 0.6f;
        public const double GapResetS = 2.0;

        private readonly int _w, _h, _n;
        private float[]? _y;                       // the output (filter state)
        private float[]? _prevRaw;
        private float[]? _lastOut;
        private float _slowFlicker;
        private double _lastT = double.NegativeInfinity;
        private int _repeats;
        // shutter tables
        private readonly float[] _before;          // 1-s running mean of the raw frames
        private bool _beforeReady;
        private int _beforeCount;
        private float[]? _beforeAtShutter;
        private float[]? _afterSum;
        private int _afterCount;
        private readonly float[] _sum;             // running sum of fine jumps
        private readonly List<float[]> _hist = [];
        private float[] _corr;
        private readonly float[] _scratch, _scratch2, _absd;

        public Denoiser(int width, int height)
        {
            _w = width;
            _h = height;
            _n = width * height;
            _before = new float[_n];
            _sum = new float[_n];
            _corr = new float[_n];
            _scratch = new float[_n];
            _scratch2 = new float[_n];
            _absd = new float[_n];
            _hist.Add(new float[_n]);
        }

        public int Shutters { get; private set; }
        public int JumpsUsed { get; private set; }
        public int JumpsRejected { get; private set; }
        /// <summary>The current table correction (degC, subtracted from each frame).</summary>
        public ReadOnlySpan<float> Correction => _corr;

        /// <summary>Clears everything (the next frame starts afresh).</summary>
        public void Reset()
        {
            _y = null;
            _prevRaw = null;
            _lastOut = null;
            _slowFlicker = 0;
            _repeats = 0;
            _beforeReady = false;
            _beforeCount = 0;
            _beforeAtShutter = null;
            _afterSum = null;
            ClearTables();
        }

        private void ClearTables()
        {
            Array.Clear(_sum);
            _hist.Clear();
            _hist.Add(new float[_n]);
            _corr = new float[_n];
        }

        /// <summary>One camera frame (degC, NaN = no reading) at monotonic time <paramref name="t"/>.
        /// Returns a new array (the same one again for a repeated frame).</summary>
        public float[] Process(float[] x, double t)
        {
            if (x.Length != _n)
                throw new ArgumentException($"expected {_n} pixels, got {x.Length}");
            if (t - _lastT > GapResetS)
                Reset();
            _lastT = t;

            // a repeated frame (the shutter): no new information
            if (_prevRaw != null && _lastOut != null && x.AsSpan().SequenceEqual(_prevRaw))
            {
                _repeats++;
                return _lastOut;
            }
            if (_repeats >= RepeatsForShutter && _beforeReady && _afterSum == null)
            {
                Shutters++;
                _beforeAtShutter = (float[])_before.Clone();
                _afterSum = new float[_n];
                _afterCount = 0;
            }
            _repeats = 0;
            _prevRaw = (float[])x.Clone();
            TrackShutter(x);

            var y = _y;
            var o = new float[_n];
            if (y == null)
            {
                for (var i = 0; i < _n; i++)
                    o[i] = x[i] - _corr[i];
                _y = (float[])o.Clone();
                _lastOut = o;
                return o;
            }

            // flicker: robust mean of (x - corr - y), fast part only
            var c = ClipSigma * SigmaC;
            double acc = 0;
            var cnt = 0;
            for (var i = 0; i < _n; i++)
            {
                var d = x[i] - _corr[i] - y[i];
                if (float.IsNaN(d))
                    continue;
                acc += Math.Clamp(d, -c, c);
                cnt++;
            }
            var cm = cnt > 0 ? (float)(acc / cnt) : 0f;
            _slowFlicker += (cm - _slowFlicker) / FlickerSlowFrames;
            var fast = cm - _slowFlicker;

            // |d2| and its 3x3 mean
            for (var i = 0; i < _n; i++)
            {
                var d2 = x[i] - _corr[i] - fast - y[i];
                _scratch[i] = d2;
                _absd[i] = float.IsNaN(d2) ? 0f : Math.Abs(d2);
            }
            Box3(_absd, _scratch2);
            var t0 = T0Sigma * SigmaC;
            var inv = 1f / ((T1Sigma - T0Sigma) * SigmaC);
            for (var i = 0; i < _n; i++)
            {
                var d2 = _scratch[i];
                if (float.IsNaN(d2))
                {
                    o[i] = x[i];            // no reading: pass it on, keep the state
                    if (!float.IsNaN(x[i]))
                        y[i] = x[i];
                    else if (float.IsNaN(y[i]))
                        y[i] = 0;
                    continue;
                }
                var a = Math.Clamp((_scratch2[i] - t0) * inv, 0f, 1f);
                var alpha = AlphaMin + (1 - AlphaMin) * a;
                y[i] += alpha * d2;
                o[i] = y[i];
            }
            _lastOut = o;
            return o;
        }

        // -- shutter tables ------------------------------------------------------------------------
        private void TrackShutter(float[] x)
        {
            if (_afterSum != null)
            {
                for (var i = 0; i < _n; i++)
                    _afterSum[i] += x[i];
                if (++_afterCount >= JumpFrames)
                {
                    var j = new float[_n];
                    for (var i = 0; i < _n; i++)
                        j[i] = _afterSum[i] / _afterCount - _beforeAtShutter![i];
                    _afterSum = null;
                    UseJump(j);
                    // the "before" average restarts on the new table
                    _beforeCount = 0;
                    _beforeReady = false;
                }
                return;
            }
            // running mean of the last ~JumpFrames frames (exponential once full)
            _beforeCount++;
            var k = 1f / Math.Min(_beforeCount, JumpFrames);
            for (var i = 0; i < _n; i++)
                _before[i] += (x[i] - _before[i]) * k;
            if (_beforeCount >= JumpFrames)
                _beforeReady = true;
        }

        private void UseJump(float[] jump)
        {
            for (var i = 0; i < _n; i++)
                if (float.IsNaN(jump[i]))
                    jump[i] = 0;
            var fine = Fine(jump);
            // does it look like a shutter's jump in a still scene?
            double ss = 0;
            float mx = 0;
            var cnt = 0;
            for (var r = FineRadius; r < _h - FineRadius; r++)
                for (var col = FineRadius; col < _w - FineRadius; col++)
                {
                    var v = fine[r * _w + col];
                    ss += v * v;
                    mx = Math.Max(mx, Math.Abs(v));
                    cnt++;
                }
            var std = (float)Math.Sqrt(ss / Math.Max(cnt, 1));
            if (std > JumpFineStdMaxC || mx > JumpFineAbsMaxC)
            {
                JumpsRejected++;
                ClearTables();
                Log.Info($"denoise: shutter {Shutters}: the scene moved during the shutter (fine jump {std * 1000:F0} mK rms); table averaging restarts");
                return;
            }
            JumpsUsed++;
            for (var i = 0; i < _n; i++)
                _sum[i] += fine[i];
            _hist.Add((float[])_sum.Clone());
            while (_hist.Count > Tables)
                _hist.RemoveAt(0);
            var corr = new float[_n];
            var m = 1f / _hist.Count;
            for (var i = 0; i < _n; i++)
            {
                float mean = 0;
                foreach (var h in _hist)
                    mean += h[i];
                corr[i] = _sum[i] - mean * m;
            }
            _corr = corr;
        }

        /// <summary>The fine part: the image minus a double box blur of radius FineRadius (edges clamped).</summary>
        public float[] Fine(float[] img)
        {
            var a = (float[])img.Clone();
            var tmp = new float[_n];
            for (var pass = 0; pass < 2; pass++)
            {
                BoxRows(a, tmp, FineRadius);
                BoxCols(tmp, a, FineRadius);
            }
            var o = new float[_n];
            for (var i = 0; i < _n; i++)
                o[i] = img[i] - a[i];
            return o;
        }

        private void BoxRows(float[] src, float[] dst, int r)
        {
            var norm = 1f / (2 * r + 1);
            for (var y = 0; y < _h; y++)
            {
                var row = y * _w;
                float s = 0;
                for (var k = -r; k <= r; k++)
                    s += src[row + Math.Clamp(k, 0, _w - 1)];
                for (var x = 0; x < _w; x++)
                {
                    dst[row + x] = s * norm;
                    s += src[row + Math.Min(x + r + 1, _w - 1)] - src[row + Math.Max(x - r, 0)];
                }
            }
        }

        private void BoxCols(float[] src, float[] dst, int r)
        {
            var norm = 1f / (2 * r + 1);
            for (var x = 0; x < _w; x++)
            {
                float s = 0;
                for (var k = -r; k <= r; k++)
                    s += src[Math.Clamp(k, 0, _h - 1) * _w + x];
                for (var y = 0; y < _h; y++)
                {
                    dst[y * _w + x] = s * norm;
                    s += src[Math.Min(y + r + 1, _h - 1) * _w + x] - src[Math.Max(y - r, 0) * _w + x];
                }
            }
        }

        /// <summary>3x3 mean, edges averaged over the pixels that exist.</summary>
        private void Box3(float[] src, float[] dst)
        {
            for (var y = 0; y < _h; y++)
            {
                int y0 = Math.Max(y - 1, 0), y1 = Math.Min(y + 1, _h - 1);
                for (var x = 0; x < _w; x++)
                {
                    int x0 = Math.Max(x - 1, 0), x1 = Math.Min(x + 1, _w - 1);
                    float s = 0;
                    for (var yy = y0; yy <= y1; yy++)
                        for (var xx = x0; xx <= x1; xx++)
                            s += src[yy * _w + xx];
                    dst[y * _w + x] = s / ((y1 - y0 + 1) * (x1 - x0 + 1));
                }
            }
        }
    }
}
