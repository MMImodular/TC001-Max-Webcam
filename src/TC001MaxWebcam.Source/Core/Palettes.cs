// TC001 Max Thermal Webcam: false-colour palettes, each a 256-entry R, G, B table (pure code).
//
// inferno is matplotlib's map (InfernoLut.cs, CC0). iron, rainbow and grey are this project's own
// colour stops, and custom uses the owner's 2-5 stops from settings.json: the stops are spread
// evenly from index 0 (coldest) to 255 (hottest) and the colours in between are interpolated
// linearly in RGB, rounded to the nearest integer.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TC001MaxWebcam.Core
{
    public static class Palettes
    {
        public static readonly string[] Names = ["inferno", "iron", "rainbow", "grey", "custom"];

        /// <summary>Colour stops of the built-in palettes other than inferno (cold to hot).</summary>
        public static readonly IReadOnlyDictionary<string, string[]> Presets = new Dictionary<string, string[]>
        {
            ["iron"] = ["#000000", "#20007a", "#8a0e8f", "#e2441c", "#fbb316", "#ffffff"],
            ["rainbow"] = ["#000080", "#0000ff", "#00ffff", "#00ff00", "#ffff00", "#ff0000"],
            ["grey"] = ["#000000", "#ffffff"],
        };

        private static readonly Dictionary<string, byte[]> Cache = [];

        /// <summary>The table for the settings' palette (768 bytes; do not modify it).</summary>
        public static byte[] Lut(WebcamSettings s)
        {
            if (s.Palette == "inferno")
                return InfernoLut.Rgb;
            var stops = s.Palette == "custom" ? s.CustomPalette
                : Presets.TryGetValue(s.Palette, out var p) ? p : null;
            if (stops == null)
                return InfernoLut.Rgb;
            var key = string.Join(",", stops);
            lock (Cache)
            {
                if (!Cache.TryGetValue(key, out var lut))
                {
                    lut = FromStops(stops.Select(ParseHex).ToArray());
                    if (Cache.Count > 32)
                        Cache.Clear();
                    Cache[key] = lut;
                }
                return lut;
            }
        }

        /// <summary>256 x R, G, B from 2 or more evenly spaced stops (see the file header).</summary>
        public static byte[] FromStops(IReadOnlyList<(byte R, byte G, byte B)> stops)
        {
            if (stops.Count < 2)
                throw new ArgumentException("a palette needs at least 2 colours", nameof(stops));
            var lut = new byte[768];
            var n = stops.Count;
            for (var i = 0; i < 256; i++)
            {
                var pos = i * (n - 1) / 255.0;
                var k = Math.Min((int)pos, n - 2);
                var f = pos - k;
                var (a, b) = (stops[k], stops[k + 1]);
                lut[3 * i] = Mix(a.R, b.R, f);
                lut[3 * i + 1] = Mix(a.G, b.G, f);
                lut[3 * i + 2] = Mix(a.B, b.B, f);
            }
            return lut;
        }

        private static byte Mix(byte a, byte b, double f) => (byte)Math.Round(a + (b - a) * f, MidpointRounding.AwayFromZero);

        /// <summary>"#rrggbb" or "rrggbb" (any case) to R, G, B; SettingsException otherwise.</summary>
        public static (byte R, byte G, byte B) ParseHex(string text)
        {
            var t = text.Trim();
            var h = t.StartsWith('#') ? t[1..] : t;
            if (h.Length != 6 || !int.TryParse(h, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
                throw new SettingsException($"'{text}' is not a colour like #ff8800");
            return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        /// <summary>The canonical form "#rrggbb" (lower case); SettingsException if invalid.</summary>
        public static string NormaliseHex(string text)
        {
            var (r, g, b) = ParseHex(text);
            return $"#{r:x2}{g:x2}{b:x2}";
        }
    }
}
