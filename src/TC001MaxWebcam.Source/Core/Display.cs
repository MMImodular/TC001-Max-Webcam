// TC001 Max Thermal Webcam: picture orientation and the calm auto range (pure code).
//
// Origin: C# ports of the author's own earlier Python code (Orientation and RangeSmoother, not
// public; no third-party code); the self-test checks
// this file against the values that code computed (tests/fixtures/reference_values.json).

using System;

namespace TC001MaxWebcam.Core
{
    /// <summary>
    /// The picture's orientation (settings.json rotate, mirror_horizontal, mirror_vertical): rotate
    /// clockwise by 0/90/180/270, then mirror left-right (FlipH), then top-bottom (FlipV). Code packs
    /// it as bits 0-1 rotate / 90, bit 2 FlipH, bit 3 FlipV (the reference values use it).
    /// </summary>
    public readonly record struct Orientation(int Rotate, bool FlipH, bool FlipV)
    {
        public static Orientation FromCode(uint code) => new((int)(code & 3) * 90, (code & 4) != 0, (code & 8) != 0);

        public uint Code => (uint)((Rotate / 90) & 3) | (FlipH ? 4u : 0u) | (FlipV ? 8u : 0u);

        public bool Identity => Rotate % 360 == 0 && !FlipH && !FlipV;

        /// <summary>Display size of a w x h sensor frame.</summary>
        public (int W, int H) DisplaySize(int w, int h) => (Rotate / 90) % 2 == 1 ? (h, w) : (w, h);

        /// <summary>Sensor pixel (x, y) of a w x h frame -> display pixel.</summary>
        public (int X, int Y) ToDisplay(int x, int y, int w, int h)
        {
            int X, Y;
            switch ((Rotate / 90) & 3)
            {
                case 1: X = h - 1 - y; Y = x; break;          // numpy rot90(k=-1): clockwise
                case 2: X = w - 1 - x; Y = h - 1 - y; break;
                case 3: X = y; Y = w - 1 - x; break;
                default: X = x; Y = y; break;
            }
            var (dw, dh) = DisplaySize(w, h);
            if (FlipH) X = dw - 1 - X;
            if (FlipV) Y = dh - 1 - Y;
            return (X, Y);
        }

        /// <summary>The displayed picture of a w x h sensor array.</summary>
        public T[] Apply<T>(T[] src, int w, int h, out int dw, out int dh)
        {
            (dw, dh) = DisplaySize(w, h);
            if (Identity)
                return src;
            var dst = new T[src.Length];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var (X, Y) = ToDisplay(x, y, w, h);
                    dst[Y * dw + X] = src[y * w + x];
                }
            }
            return dst;
        }
    }

    public static class Stats
    {
        /// <summary>numpy.percentile(values, 1.0) for float32 values (linear method, float32 interpolation).
        /// Sorts <paramref name="values"/> in place. NaN-free input.</summary>
        public static float Percentile1(float[] values, int count)
        {
            if (count <= 0)
                return float.NaN;
            Array.Sort(values, 0, count);
            var v = (count - 1) * 0.01;
            var i = (int)Math.Floor(v);
            if (i + 1 >= count)
                return values[count - 1];
            var g = (float)(v - i);
            float lo = values[i], hi = values[i + 1];
            var d = hi - lo;
            return g >= 0.5f ? hi - d * (1f - g) : lo + d * g;
        }
    }

    /// <summary>
    /// A calm auto range for live views (port of the original Python RangeSmoother): whole degrees,
    /// no jitter, quick to show a new hot spot. Per frame the target is the 1st percentile of a
    /// 2x-subsampled frame .. the hottest pixel. Widening is immediate; narrowing follows an exponential
    /// average with time constant tau_s (<see cref="TauS"/>, the range_speed setting; 0 = at once).
    /// The shown range is rounded outwards to whole degrees and only narrows once it is
    /// hysteresis_c wider than needed.
    /// </summary>
    public sealed class RangeSmoother(double tauS = 4.0, double hysteresisC = 1.0, double minSpanC = 5.0)
    {
        /// <summary>The narrowing time constant in seconds; 0 narrows at once.</summary>
        public double TauS { get; set; } = tauS;

        private (double Lo, double Hi)? _s;
        private (double Lo, double Hi)? _shown;
        private double? _t;
        private float[] _scratch = [];

        public (double Lo, double Hi)? Shown => _shown;

        public void Reset()
        {
            _s = _shown = null;
            _t = null;
        }

        private (double Lo, double Hi)? Target(float[] temp, int w, int h)
        {
            var need = ((w + 1) / 2) * ((h + 1) / 2);
            if (_scratch.Length < need)
                _scratch = new float[need];
            var n = 0;
            for (var y = 0; y < h; y += 2)
            {
                for (var x = 0; x < w; x += 2)
                {
                    var v = temp[y * w + x];
                    if (float.IsFinite(v))
                        _scratch[n++] = v;
                }
            }
            if (n == 0)
                return null;
            var max = float.NegativeInfinity;
            foreach (var v in temp)
            {
                if (v > max) max = v; // NaN compares false: nanmax
            }
            return (Stats.Percentile1(_scratch, n), max);
        }

        /// <summary>Feeds a frame taken at time t (s); returns the range to show.</summary>
        public (double Lo, double Hi) Update(float[] temp, int w, int h, double t)
        {
            var target = Target(temp, w, h);
            if (target is not { } tg)
                return _shown ?? (0.0, minSpanC);
            double sLo, sHi;
            if (_s is not { } s || _t is not { } t0)
            {
                (sLo, sHi) = tg;
            }
            else
            {
                var a = TauS <= 0 ? 1.0 : 1.0 - Math.Exp(-Math.Max(t - t0, 0.0) / TauS);
                (sLo, sHi) = s;
                sLo = tg.Lo < sLo ? tg.Lo : sLo + a * (tg.Lo - sLo);
                sHi = tg.Hi > sHi ? tg.Hi : sHi + a * (tg.Hi - sHi);
            }
            _s = (sLo, sHi);
            _t = t;
            double loC = Math.Floor(sLo), hiC = Math.Ceiling(sHi);
            if (hiC - loC < minSpanC)
            {
                var mid = (sLo + sHi) / 2;
                loC = Math.Floor(mid - minSpanC / 2);
                hiC = loC + Math.Ceiling(minSpanC);
            }
            if (_shown is not { } shown)
            {
                _shown = (loC, hiC);
                return _shown.Value;
            }
            var (lo, hi) = shown;
            if (loC < lo || loC > lo + hysteresisC)
                lo = loC;
            if (hiC > hi || hiC < hi - hysteresisC)
                hi = hiC;
            if (hi - lo < minSpanC)
                hi = lo + Math.Ceiling(minSpanC);
            _shown = (lo, hi);
            return _shown.Value;
        }
    }
}
