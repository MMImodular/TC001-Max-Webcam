// TC001 Max Thermal Webcam: the tray icon and the menu's palette swatches, drawn as pixels (pure code).
//
// The icon is a tiny thermal picture after one of the owner's screenshots: a warm figure behind a
// glowing hot cup, with the webcam's green hot-spot cross. It is drawn as a temperature field
// coloured with the current palette, so the icon follows the palette setting. Supersampled 4 x 4
// for smooth edges. Output: n x n BGRA, top-down, premultiplied alpha (what CreateIconIndirect
// wants for a 32-bit icon). Origin: written for this project.

using System;

namespace TC001MaxWebcam.Core
{
    public static class TrayArt
    {
        private static readonly (byte R, byte G, byte B) Green = (0, 255, 96);

        /// <summary>The icon, <paramref name="n"/> x <paramref name="n"/> pixels, in the colours of
        /// <paramref name="lut"/> (a palette table, 768 bytes).</summary>
        public static byte[] Icon(int n, byte[] lut)
        {
            if (n < 8)
                throw new ArgumentOutOfRangeException(nameof(n), "at least 8 pixels");
            const int ss = 4;
            var px = new byte[n * n * 4];
            var stroke = Math.Max(1.0 / n, 0.06);        // the cross's arms, at least one pixel
            var edge = Math.Max(1.0 / n, 0.03);          // its dark outline
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    int r = 0, g = 0, b = 0, covered = 0;
                    for (var sy = 0; sy < ss; sy++)
                    {
                        for (var sx = 0; sx < ss; sx++)
                        {
                            var u = (x + (sx + 0.5) / ss) / n;
                            var v = (y + (sy + 0.5) / ss) / n;
                            if (RoundedBox(u, v, 0.5, 0.5, 0.5, 0.5, 0.18) > 0)
                                continue; // outside the rounded square: transparent
                            covered++;
                            (byte R, byte G, byte B) c;
                            var cross = Cross(u, v, stroke, edge);
                            if (cross == 2)
                                c = Green;
                            else if (cross == 1)
                                c = (0, 0, 0);
                            else
                            {
                                var i = (int)Math.Clamp(Field(u, v) * 255.0, 0, 255) * 3;
                                c = (lut[i], lut[i + 1], lut[i + 2]);
                            }
                            r += c.R; g += c.G; b += c.B;
                        }
                    }
                    var o = (y * n + x) * 4;
                    const int all = ss * ss;
                    // premultiplied: the sums over all samples (uncovered ones count as black)
                    px[o] = (byte)(b / all);
                    px[o + 1] = (byte)(g / all);
                    px[o + 2] = (byte)(r / all);
                    px[o + 3] = (byte)(covered * 255 / all);
                }
            }
            return px;
        }

        /// <summary>A w x h swatch of the palette (cold left, hot right), BGRA, opaque.</summary>
        public static byte[] Swatch(int w, int h, byte[] lut)
        {
            var px = new byte[w * h * 4];
            for (var x = 0; x < w; x++)
            {
                var i = (w == 1 ? 255 : x * 255 / (w - 1)) * 3;
                for (var y = 0; y < h; y++)
                {
                    var o = (y * w + x) * 4;
                    var border = y == 0 || y == h - 1 || x == 0 || x == w - 1;
                    px[o] = border ? (byte)80 : lut[i + 2];
                    px[o + 1] = border ? (byte)80 : lut[i + 1];
                    px[o + 2] = border ? (byte)80 : lut[i];
                    px[o + 3] = 255;
                }
            }
            return px;
        }

        /// <summary>The scene as a 0..1 "temperature": cold background, a warm head and shoulders,
        /// and a hot cup in front.</summary>
        private static double Field(double u, double v)
        {
            var t = 0.06 + 0.05 * v;
            var head = Math.Sqrt(Sq((u - 0.5) / 0.34) + Sq((v - 0.40) / 0.40));
            t = Math.Max(t, 0.42 * Math.Clamp((1.15 - head) / 0.35, 0, 1));
            var d = RoundedBox(u, v, 0.56, 0.64, 0.24, 0.30, 0.06);
            var cup = d <= 0 ? 0.80 + 0.20 * Math.Min(1, -d / 0.12) : 0.80 * Math.Max(0, 1 - d / 0.07);
            return Math.Max(t, cup);
        }

        /// <summary>2 on the green cross, 1 on its outline, 0 elsewhere.</summary>
        private static int Cross(double u, double v, double stroke, double edge)
        {
            const double cx = 0.47, cy = 0.62, arm = 0.24;
            bool On(double half, double len) =>
                (Math.Abs(u - cx) <= len && Math.Abs(v - cy) <= half) || (Math.Abs(v - cy) <= len && Math.Abs(u - cx) <= half);
            if (On(stroke / 2, arm))
                return 2;
            return On(stroke / 2 + edge, arm + edge) ? 1 : 0;
        }

        /// <summary>Signed distance from (u, v) to a rounded box (negative inside).</summary>
        private static double RoundedBox(double u, double v, double cx, double cy, double hw, double hh, double radius)
        {
            var dx = Math.Abs(u - cx) - hw + radius;
            var dy = Math.Abs(v - cy) - hh + radius;
            var outside = Math.Sqrt(Sq(Math.Max(dx, 0)) + Sq(Math.Max(dy, 0)));
            var inside = Math.Min(Math.Max(dx, dy), 0);
            return outside + inside - radius;
        }

        private static double Sq(double a) => a * a;
    }
}
