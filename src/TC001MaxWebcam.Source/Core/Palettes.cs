// TC001 Max Thermal Webcam: false-colour palettes, each a 256-entry R, G, B table (pure code).
//
// The same palettes as the owner's labbench (src/labbench/thermal/view.py, unpublished), so the
// webcam and labbench show the same colours: inferno is matplotlib's map (InfernoLut.cs, CC0);
// rainbow is Google's turbo map (TurboLut.cs, Apache-2.0); iron's colour stops and the
// black-hot palette come from labbench; grey is labbench's "white hot". custom uses the owner's
// 2-5 stops from settings.json. Stops are spread evenly from index 0 (coldest) to 255 (hottest)
// and the colours in between are interpolated linearly in RGB, rounded to the nearest integer.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TC001MaxWebcam.Core
{
    public static class Palettes
    {
        public static readonly string[] Names = ["inferno", "iron", "rainbow", "grey", "black_hot", "custom"];

        /// <summary>Colour stops of the built-in palettes other than inferno and rainbow (tables),
        /// cold to hot. All from labbench.</summary>
        public static readonly IReadOnlyDictionary<string, string[]> Presets = new Dictionary<string, string[]>
        {
            ["iron"] = ["#000000", "#20008c", "#9b009b", "#e1301e", "#ff9600", "#ffe650", "#ffffff"],
            ["grey"] = ["#000000", "#ffffff"],
            ["black_hot"] = ["#ffffff", "#000000"],
        };

        /// <summary>The name people see (the setting keeps the short name).</summary>
        public static string Label(string name) => name switch
        {
            "inferno" => "Inferno",
            "iron" => "Iron",
            "rainbow" => "Rainbow",
            "grey" => "White hot",
            "black_hot" => "Black hot",
            "custom" => "Custom",
            _ => name,
        };

        /// <summary>A palette name as typed (any case; labbench's and other common names too) to
        /// its setting name, or null if unknown.</summary>
        public static string? Canonical(string text) => text.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_') switch
        {
            "grey" or "gray" or "white_hot" or "whitehot" => "grey",
            "black_hot" or "blackhot" => "black_hot",
            "rainbow" or "turbo" => "rainbow",
            "iron" or "ironbow" => "iron",
            var n when Names.Contains(n) => n,
            _ => null,
        };

        private static readonly Dictionary<string, byte[]> Cache = [];

        /// <summary>The table for the settings' palette (768 bytes; do not modify it).</summary>
        public static byte[] Lut(WebcamSettings s)
        {
            if (s.Palette == "inferno")
                return InfernoLut.Rgb;
            if (s.Palette == "rainbow")
                return TurboLut.Rgb;
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
