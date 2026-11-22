// TC001 Max Thermal Webcam: draws the 640 x 480 BGRA picture (pure code).
//
// The thermal picture in the settings' palette over the settings' range (auto: the calm auto range
// of RangeSmoother - 1st percentile .. hottest pixel, whole degrees, widens at once, narrows slowly;
// locked: the first frame's auto range, held; manual: range_min .. range_max), rotated and mirrored
// as the settings say, a green + with its temperature on the hottest pixel, a small x at the centre,
// max / centre / hot-spot temperatures, the colour scale, the time, and a calm badge while the
// camera calibrates. The settings' overlay switches hide the marks and bars (overlay=false hides
// them all); the badge always shows. All readouts are in the settings' units. Cards (no camera,
// camera in use, ...) are drawn with the same pixel font.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace TC001MaxWebcam.Core
{
    public enum PictureKind { Live, Card }

    /// <summary>What to show; built by <see cref="WebcamEngine"/>, drawn by <see cref="FrameRenderer"/>.</summary>
    public sealed class Picture
    {
        public PictureKind Kind { get; init; }
        /// <summary>Changes whenever the content changes (the renderer skips unchanged pictures).</summary>
        public long Version { get; init; }
        public float[]? Temperature { get; init; }     // degC, sensor orientation, row-major
        public int Width { get; init; }
        public int Height { get; init; }
        public WebcamSettings Settings { get; init; } = WebcamSettings.Default;
        /// <summary>Changes whenever the settings change (the renderer then starts its range afresh).</summary>
        public long SettingsVersion { get; init; }
        public double TWall { get; init; }             // frame time, Unix seconds
        public string Source { get; init; } = "";
        public string? Badge { get; init; }            // e.g. "CALIBRATING (SHUTTER)"
        public bool BadgeCalm { get; init; } = true;   // amber (calibrating) vs grey (reconnecting)
        public bool Dim { get; init; }
        public string CardTitle { get; init; } = "";
        public string CardText { get; init; } = "";

        public static Picture Card(long version, string title, string text) =>
            new() { Kind = PictureKind.Card, Version = version, CardTitle = title, CardText = text };
    }

    public sealed class FrameRenderer
    {
        public const int OutW = 640;
        public const int OutH = 480;
        public const int OutBytes = OutW * OutH * 4;
        public const string Footer = "TC001 MAX THERMAL WEBCAM";

        private static readonly (byte R, byte G, byte B) Green = (0, 255, 96);

        private long? _settingsVersion;
        private (double Lo, double Hi)? _locked;
        private float[]? _scratch;

        public RangeSmoother Smoother { get; } = new();

        /// <summary>The colour range last used (in the settings' units).</summary>
        public (double Lo, double Hi) LastRange { get; private set; }

        /// <summary>Where the hot-spot marker was drawn last (output pixels), for tests.</summary>
        public (int X, int Y)? LastHotSpotPx { get; private set; }

        /// <summary>The picture's area in the output (letterboxed), for tests.</summary>
        public (int X, int Y, int W, int H) LastImageRect { get; private set; }

        /// <summary>The palette table last used, for tests.</summary>
        public byte[] LastLut { get; private set; } = InfernoLut.Rgb;

        /// <summary>Forget the auto / locked range (a new stream, or new settings).</summary>
        public void ResetRange()
        {
            Smoother.Reset();
            _locked = null;
        }

        public void Render(Picture pic, byte[] bgra)
        {
            if (bgra.Length < OutBytes)
                throw new ArgumentException("output buffer too small", nameof(bgra));
            if (pic.Kind == PictureKind.Card || pic.Temperature == null)
            {
                RenderCard(pic.CardTitle, pic.CardText, bgra);
                return;
            }
            RenderLive(pic, bgra);
        }

        /// <summary>The colour range for this frame (in display units) under the settings' range mode.</summary>
        private (double Lo, double Hi) Range(WebcamSettings st, float[] temp, int w, int h, double t)
        {
            switch (st.Range)
            {
                case RangeMode.Manual:
                    return (st.RangeMin, st.RangeMax);
                case RangeMode.Locked:
                    _locked ??= Smoother.Update(temp, w, h, t);
                    return _locked.Value;
                default:
                    return Smoother.Update(temp, w, h, t);
            }
        }

        // -- live picture --------------------------------------------------------------------------
        private void RenderLive(Picture pic, byte[] bgra)
        {
            var st = pic.Settings;
            if (_settingsVersion != pic.SettingsVersion)
            {
                ResetRange();
                _settingsVersion = pic.SettingsVersion;
            }
            var lut = Palettes.Lut(st);
            LastLut = lut;
            var unit = st.UnitSymbol;

            Fill(bgra, 0, 0, OutW, OutH, 0, 0, 0, 255);
            var temp = Temperature.FrameToUnits(pic.Temperature!, st.Units, ref _scratch);
            int sw = pic.Width, sh = pic.Height;
            var o = st.Orientation;
            var shown = o.Apply(temp, sw, sh, out var dw, out var dh);

            var (lo, hi) = Range(st, temp, sw, sh, pic.TWall);
            LastRange = (lo, hi);

            var s = Math.Min((double)OutW / dw, (double)OutH / dh);
            var ow = Math.Max(1, (int)Math.Round(dw * s, MidpointRounding.ToEven));
            var oh = Math.Max(1, (int)Math.Round(dh * s, MidpointRounding.ToEven));
            int x0 = (OutW - ow) / 2, y0 = (OutH - oh) / 2;
            LastImageRect = (x0, y0, ow, oh);
            DrawFalseColour(shown, dw, dh, lo, hi, x0, y0, ow, oh, s, pic.Dim, lut, bgra);

            (double X, double Y) ToPx(double x, double y) => (x0 + (x + 0.5) * s, y0 + (y + 0.5) * s);

            var overlay = st.Overlay;

            // centre marker
            if (overlay && st.Crosshair)
            {
                var (cx, cy) = ToPx(dw / 2, dh / 2);
                for (var d = -5; d <= 5; d++)
                {
                    Put(bgra, (int)cx + d, (int)cy + d, 255, 255, 255);
                    Put(bgra, (int)cx + d, (int)cy - d, 255, 255, 255);
                }
            }

            // statistics in sensor orientation
            float max = float.NegativeInfinity;
            var hottest = -1;
            for (var i = 0; i < temp.Length; i++)
            {
                if (temp[i] > max) { max = temp[i]; hottest = i; }
            }
            var centre = temp[(sh / 2) * sw + sw / 2];
            float? hotV = hottest >= 0 ? temp[hottest] : null;

            LastHotSpotPx = null;
            if (hottest >= 0 && overlay && st.HotSpot)
            {
                var (X, Y) = o.ToDisplay(hottest % sw, hottest / sw, sw, sh);
                var (pxd, pyd) = ToPx(X, Y);
                int px = (int)pxd, py = (int)pyd;
                LastHotSpotPx = (px, py);
                Fill(bgra, px - 13, py - 2, 27, 4, 0, 0, 0, 255);
                Fill(bgra, px - 2, py - 13, 4, 27, 0, 0, 0, 255);
                Fill(bgra, px - 12, py - 1, 25, 2, Green.R, Green.G, Green.B, 255);
                Fill(bgra, px - 1, py - 12, 2, 25, Green.R, Green.G, Green.B, 255);
                var label = Fmt(hotV, unit);
                var tx = px + 110 < OutW ? px + 10 : px - 110;
                var ty = py - 30 > 34 ? py - 26 : py + 10;
                Text(bgra, tx, ty, label, 2, Green.R, Green.G, Green.B, outline: true);
            }

            // top bar: temperatures
            if (overlay && st.Readouts)
            {
                Fill(bgra, 0, 0, OutW, 30, 0, 0, 0, 150);
                Text(bgra, 8, 8, $"MAX {Fmt(float.IsFinite(max) ? max : null, unit)}  CENTRE {Fmt(float.IsFinite(centre) ? centre : null, unit)}",
                     2, 255, 255, 255, outline: false);
            }
            if (overlay && st.Scale)
                DrawScaleBar(bgra, pic, st, lut, lo, hi, unit);

            if (pic.Badge != null)
            {
                var text = PixelFont.Normalise(pic.Badge);
                var w = PixelFont.Measure(text, 3) + 24;
                var bh = PixelFont.Height(3) + 18;
                int bx = (OutW - w) / 2, by = (OutH - bh) / 2;
                if (pic.BadgeCalm)
                    Fill(bgra, bx, by, w, bh, 230, 150, 0, 230);
                else
                    Fill(bgra, bx, by, w, bh, 110, 110, 120, 230);
                Text(bgra, bx + 12, by + 9, text, 3, 0, 0, 0, outline: false);
            }
        }

        private static void DrawScaleBar(byte[] bgra, Picture pic, WebcamSettings st, byte[] lut, double lo, double hi, string unit)
        {
            // bottom bar: colour scale, range mode, time, source
            const int barY = OutH - 26;
            Fill(bgra, 0, barY, OutW, 26, 0, 0, 0, 150);
            var loText = Whole(lo) + unit;
            var x = 8;
            Text(bgra, x, barY + 6, loText, 2, 220, 220, 220, outline: false);
            x += PixelFont.Measure(loText, 2) + 6;
            for (var i = 0; i < 100; i++)
            {
                var idx = i * 255 / 99 * 3;
                Fill(bgra, x + i, barY + 7, 1, 12, lut[idx], lut[idx + 1], lut[idx + 2], 255);
            }
            x += 106;
            var hiText = Whole(hi) + unit;
            Text(bgra, x, barY + 6, hiText, 2, 220, 220, 220, outline: false);
            x += PixelFont.Measure(hiText, 2) + 8;
            if (st.Range != RangeMode.Auto)
                Text(bgra, x, barY + 10, st.Range == RangeMode.Locked ? "LOCKED" : "MANUAL", 1, 200, 200, 200, outline: false);
            if (pic.TWall > 0)
            {
                var stamp = DateTimeOffset.FromUnixTimeMilliseconds((long)(pic.TWall * 1000)).ToLocalTime()
                    .ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                Text(bgra, 360, barY + 6, stamp, 2, 220, 220, 220, outline: false);
            }
            if (pic.Source.Length > 0)
            {
                var src = PixelFont.Normalise(pic.Source);
                Text(bgra, OutW - 6 - PixelFont.Measure(src, 1), barY + 10, src, 1, 200, 200, 200, outline: false);
            }
        }

        private static void DrawFalseColour(float[] t, int w, int h, double lo, double hi, int x0, int y0, int ow, int oh,
                                            double s, bool dim, byte[] lut, byte[] bgra)
        {
            var scale = 255.0 / Math.Max(hi - lo, 1e-6);
            var fx = new float[ow];
            var ix = new int[ow];
            for (var X = 0; X < ow; X++)
            {
                var u = Math.Clamp((X + 0.5) / s - 0.5, 0, w - 1);
                ix[X] = Math.Min((int)u, Math.Max(w - 2, 0));
                fx[X] = (float)(u - ix[X]);
            }
            var flo = (float)lo;
            for (var Y = 0; Y < oh; Y++)
            {
                var v = Math.Clamp((Y + 0.5) / s - 0.5, 0, h - 1);
                var iy = Math.Min((int)v, Math.Max(h - 2, 0));
                var fy = (float)(v - iy);
                var r0 = iy * w;
                var r1 = Math.Min(iy + 1, h - 1) * w;
                var o = ((y0 + Y) * OutW + x0) * 4;
                for (var X = 0; X < ow; X++, o += 4)
                {
                    var a = ix[X];
                    var b = Math.Min(a + 1, w - 1);
                    float t00 = N(t[r0 + a], flo), t01 = N(t[r0 + b], flo), t10 = N(t[r1 + a], flo), t11 = N(t[r1 + b], flo);
                    var top = t00 + (t01 - t00) * fx[X];
                    var bot = t10 + (t11 - t10) * fx[X];
                    var val = top + (bot - top) * fy;
                    var idx = (int)Math.Clamp((val - lo) * scale, 0, 255) * 3;
                    byte rr = lut[idx], gg = lut[idx + 1], bb = lut[idx + 2];
                    if (dim)
                    {
                        rr = (byte)(rr * 45 / 100); gg = (byte)(gg * 45 / 100); bb = (byte)(bb * 45 / 100);
                    }
                    bgra[o] = bb;
                    bgra[o + 1] = gg;
                    bgra[o + 2] = rr;
                    bgra[o + 3] = 255;
                }
            }
        }

        private static float N(float v, float lo) => float.IsFinite(v) ? v : lo;

        private static string Fmt(float? v, string unit) =>
            v is { } f && float.IsFinite(f) ? f.ToString("F1", CultureInfo.InvariantCulture) + unit : "-";

        /// <summary>Whole degrees as such ("25"), others with one decimal ("25.5").</summary>
        private static string Whole(double v) =>
            v.ToString(Math.Abs(v - Math.Round(v)) < 1e-9 ? "F0" : "F1", CultureInfo.InvariantCulture);

        // -- cards ---------------------------------------------------------------------------------
        public static void RenderCard(string title, string text, byte[] bgra)
        {
            Fill(bgra, 0, 0, OutW, OutH, 0x20, 0x24, 0x30, 255);
            Fill(bgra, 0, 0, OutW, 8, 255, 176, 0, 255);
            Fill(bgra, 0, OutH - 8, OutW, 8, 255, 176, 0, 255);
            var y = 110;
            foreach (var line in Wrap(PixelFont.Normalise(title), 4))
            {
                Text(bgra, (OutW - PixelFont.Measure(line, 4)) / 2, y, line, 4, 255, 255, 255, outline: false);
                y += 38;
            }
            y += 20;
            foreach (var para in text.Split('\n'))
            {
                foreach (var line in Wrap(PixelFont.Normalise(para), 2))
                {
                    Text(bgra, (OutW - PixelFont.Measure(line, 2)) / 2, y, line, 2, 0xC0, 0xC0, 0xC8, outline: false);
                    y += 22;
                }
            }
            Text(bgra, (OutW - PixelFont.Measure(Footer, 2)) / 2, OutH - 44, Footer, 2, 0x90, 0x90, 0x98, outline: false);
        }

        /// <summary>Word-wraps to the output width minus margins at <paramref name="scale"/>.</summary>
        public static List<string> Wrap(string text, int scale)
        {
            var max = (OutW - 40) / ((PixelFont.GlyphW + 1) * scale);
            var lines = new List<string>();
            var cur = "";
            foreach (var word in text.Split(' '))
            {
                var w = word;
                while (w.Length > max)
                {
                    if (cur.Length > 0) { lines.Add(cur); cur = ""; }
                    lines.Add(w[..max]);
                    w = w[max..];
                }
                if (cur.Length == 0) cur = w;
                else if (cur.Length + 1 + w.Length <= max) cur += " " + w;
                else { lines.Add(cur); cur = w; }
            }
            lines.Add(cur);
            return lines;
        }

        // -- drawing primitives --------------------------------------------------------------------
        public static void Fill(byte[] bgra, int x, int y, int w, int h, byte r, byte g, byte b, int alpha)
        {
            int xa = Math.Max(x, 0), ya = Math.Max(y, 0), xb = Math.Min(x + w, OutW), yb = Math.Min(y + h, OutH);
            for (var yy = ya; yy < yb; yy++)
            {
                var o = (yy * OutW + xa) * 4;
                for (var xx = xa; xx < xb; xx++, o += 4)
                {
                    if (alpha >= 255)
                    {
                        bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r;
                    }
                    else
                    {
                        bgra[o] = (byte)((bgra[o] * (255 - alpha) + b * alpha) / 255);
                        bgra[o + 1] = (byte)((bgra[o + 1] * (255 - alpha) + g * alpha) / 255);
                        bgra[o + 2] = (byte)((bgra[o + 2] * (255 - alpha) + r * alpha) / 255);
                    }
                    bgra[o + 3] = 255;
                }
            }
        }

        private static void Put(byte[] bgra, int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= OutW || y >= OutH)
                return;
            var o = (y * OutW + x) * 4;
            bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = 255;
        }

        /// <summary>Draws text with the pixel font (upper case), optionally with a black outline.</summary>
        public static void Text(byte[] bgra, int x, int y, string text, int scale, byte r, byte g, byte b, bool outline)
        {
            var t = PixelFont.Normalise(text);
            if (outline)
            {
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (dx != 0 || dy != 0)
                            Glyphs(bgra, x + dx * Math.Max(1, scale / 2), y + dy * Math.Max(1, scale / 2), t, scale, 0, 0, 0);
            }
            Glyphs(bgra, x, y, t, scale, r, g, b);
        }

        private static void Glyphs(byte[] bgra, int x, int y, string t, int scale, byte r, byte g, byte b)
        {
            for (var i = 0; i < t.Length; i++)
            {
                var gx0 = x + i * (PixelFont.GlyphW + 1) * scale;
                for (var gy = 0; gy < PixelFont.GlyphH; gy++)
                    for (var gx = 0; gx < PixelFont.GlyphW; gx++)
                        if (PixelFont.Pixel(t[i], gx, gy))
                            Fill(bgra, gx0 + gx * scale, y + gy * scale, scale, scale, r, g, b, 255);
            }
        }
    }
}
