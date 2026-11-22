// TC001 Max Thermal Webcam: a tiny 5 x 7 bitmap font for the picture's labels (our own glyphs;
// upper case, digits and a little punctuation). Pure code, so the labels need no Direct2D in
// the Frame Server and the self-test can check what is drawn.

using System;
using System.Collections.Generic;

namespace TC001MaxWebcam.Core
{
    public static class PixelFont
    {
        public const int GlyphW = 5;
        public const int GlyphH = 7;

        private static readonly Dictionary<char, byte[]> Glyphs = Build();

        private static Dictionary<char, byte[]> Build()
        {
            var src = new (char C, string Rows)[]
            {
                ('A', ".###.|#...#|#...#|#####|#...#|#...#|#...#"),
                ('B', "####.|#...#|#...#|####.|#...#|#...#|####."),
                ('C', ".###.|#...#|#....|#....|#....|#...#|.###."),
                ('D', "####.|#...#|#...#|#...#|#...#|#...#|####."),
                ('E', "#####|#....|#....|####.|#....|#....|#####"),
                ('F', "#####|#....|#....|####.|#....|#....|#...."),
                ('G', ".###.|#...#|#....|#.###|#...#|#...#|.####"),
                ('H', "#...#|#...#|#...#|#####|#...#|#...#|#...#"),
                ('I', ".###.|..#..|..#..|..#..|..#..|..#..|.###."),
                ('J', "..###|...#.|...#.|...#.|...#.|#..#.|.##.."),
                ('K', "#...#|#..#.|#.#..|##...|#.#..|#..#.|#...#"),
                ('L', "#....|#....|#....|#....|#....|#....|#####"),
                ('M', "#...#|##.##|#.#.#|#.#.#|#...#|#...#|#...#"),
                ('N', "#...#|#...#|##..#|#.#.#|#..##|#...#|#...#"),
                ('O', ".###.|#...#|#...#|#...#|#...#|#...#|.###."),
                ('P', "####.|#...#|#...#|####.|#....|#....|#...."),
                ('Q', ".###.|#...#|#...#|#...#|#.#.#|#..#.|.##.#"),
                ('R', "####.|#...#|#...#|####.|#.#..|#..#.|#...#"),
                ('S', ".####|#....|#....|.###.|....#|....#|####."),
                ('T', "#####|..#..|..#..|..#..|..#..|..#..|..#.."),
                ('U', "#...#|#...#|#...#|#...#|#...#|#...#|.###."),
                ('V', "#...#|#...#|#...#|#...#|#...#|.#.#.|..#.."),
                ('W', "#...#|#...#|#...#|#.#.#|#.#.#|#.#.#|.#.#."),
                ('X', "#...#|#...#|.#.#.|..#..|.#.#.|#...#|#...#"),
                ('Y', "#...#|#...#|.#.#.|..#..|..#..|..#..|..#.."),
                ('Z', "#####|....#|...#.|..#..|.#...|#....|#####"),
                ('0', ".###.|#...#|#..##|#.#.#|##..#|#...#|.###."),
                ('1', "..#..|.##..|..#..|..#..|..#..|..#..|.###."),
                ('2', ".###.|#...#|....#|...#.|..#..|.#...|#####"),
                ('3', "#####|...#.|..#..|...#.|....#|#...#|.###."),
                ('4', "...#.|..##.|.#.#.|#..#.|#####|...#.|...#."),
                ('5', "#####|#....|####.|....#|....#|#...#|.###."),
                ('6', "..##.|.#...|#....|####.|#...#|#...#|.###."),
                ('7', "#####|....#|...#.|..#..|.#...|.#...|.#..."),
                ('8', ".###.|#...#|#...#|.###.|#...#|#...#|.###."),
                ('9', ".###.|#...#|#...#|.####|....#|...#.|.##.."),
                (' ', ".....|.....|.....|.....|.....|.....|....."),
                ('.', ".....|.....|.....|.....|.....|.##..|.##.."),
                (',', ".....|.....|.....|.....|.##..|..#..|.#..."),
                (':', ".....|.##..|.##..|.....|.##..|.##..|....."),
                ('-', ".....|.....|.....|#####|.....|.....|....."),
                ('+', ".....|..#..|..#..|#####|..#..|..#..|....."),
                ('/', ".....|....#|...#.|..#..|.#...|#....|....."),
                ('(', "...#.|..#..|.#...|.#...|.#...|..#..|...#."),
                (')', ".#...|..#..|...#.|...#.|...#.|..#..|.#..."),
                ('°', ".##..|#..#.|#..#.|.##..|.....|.....|....."),
                ('%', "##...|##..#|...#.|..#..|.#...|#..##|...##"),
                ('!', "..#..|..#..|..#..|..#..|..#..|.....|..#.."),
                ('?', ".###.|#...#|....#|...#.|..#..|.....|..#.."),
                ('\'', "..#..|..#..|.#...|.....|.....|.....|....."),
                ('_', ".....|.....|.....|.....|.....|.....|#####"),
                ('=', ".....|.....|#####|.....|#####|.....|....."),
                ('#', ".#.#.|#####|.#.#.|.#.#.|.#.#.|#####|.#.#."),
                ('<', "...#.|..#..|.#...|#....|.#...|..#..|...#."),
                ('>', ".#...|..#..|...#.|....#|...#.|..#..|.#..."),
            };
            var d = new Dictionary<char, byte[]>();
            foreach (var (c, rows) in src)
            {
                var parts = rows.Split('|');
                if (parts.Length != GlyphH)
                    throw new InvalidOperationException($"glyph {c}: {parts.Length} rows");
                var g = new byte[GlyphH];
                for (var r = 0; r < GlyphH; r++)
                {
                    if (parts[r].Length != GlyphW)
                        throw new InvalidOperationException($"glyph {c} row {r}");
                    for (var x = 0; x < GlyphW; x++)
                    {
                        if (parts[r][x] == '#')
                            g[r] |= (byte)(1 << (GlyphW - 1 - x));
                    }
                }
                d[c] = g;
            }
            return d;
        }

        public static bool Has(char c) => Glyphs.ContainsKey(char.ToUpperInvariant(c));

        /// <summary>Upper-cased text with unknown characters replaced by '?'.</summary>
        public static string Normalise(string text)
        {
            var chars = text.Replace("…", "...").Replace("–", "-").ToUpperInvariant().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (!Glyphs.ContainsKey(chars[i]))
                    chars[i] = '?';
            }
            return new string(chars);
        }

        /// <summary>Width in pixels of <paramref name="text"/> at <paramref name="scale"/> (1 pixel gap per glyph column).</summary>
        public static int Measure(string text, int scale) => text.Length == 0 ? 0 : (text.Length * (GlyphW + 1) - 1) * scale;

        public static int Height(int scale) => GlyphH * scale;

        /// <summary>Is glyph pixel (gx, gy) of <paramref name="c"/> set?</summary>
        public static bool Pixel(char c, int gx, int gy)
        {
            if (!Glyphs.TryGetValue(char.ToUpperInvariant(c), out var g))
                g = Glyphs['?'];
            return gx >= 0 && gx < GlyphW && gy >= 0 && gy < GlyphH && (g[gy] & (1 << (GlyphW - 1 - gx))) != 0;
        }
    }
}
