// TC001 Max Thermal Webcam: self-test of src\TC001MaxWebcam.Source\Core (no hardware, no packages).
//
//   TC001MaxWebcamTests <fixtures dir>     (build.ps1 passes tests\fixtures)
//
// The protocol tests run on real bytes from a TC001 Max (tests/fixtures/README.md); the reference
// tests compare with values the original Python implementation computed (reference_values.json);
// the settings, palette and picture tests check the renderer's options; the session and engine
// tests drive the reader's state machine with a fake USB link and a fake clock.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using TC001MaxWebcam.Core;

namespace TC001MaxWebcam.Tests
{
    internal static class Program
    {
        private const int Margin = 4096;
        private static string _fix = "";
        private static int _passed;
        private static readonly List<string> Failures = [];

        private static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); // decimal commas must not leak into the picture or settings
            _fix = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "fixtures");
            if (!File.Exists(Path.Combine(_fix, "reference_values.json")))
            {
                Console.Error.WriteLine($"fixtures not found in {_fix} (pass tests\\fixtures)");
                return 2;
            }
            var logs = new List<string>();
            Log.Sink = (level, msg) => { lock (logs) logs.Add($"{level}: {msg}"); };

            foreach (var (name, test) in Tests())
            {
                try
                {
                    test();
                    _passed++;
                    Console.WriteLine($"ok   {name}");
                }
                catch (Exception e)
                {
                    Failures.Add($"{name}: {e.Message}");
                    Console.WriteLine($"FAIL {name}: {e}");
                }
            }
            Console.WriteLine($"TC001MaxWebcamTests: {_passed} passed, {Failures.Count} failed");
            foreach (var f in Failures)
                Console.WriteLine("  " + f);
            return Failures.Count == 0 ? 0 : 1;
        }

        // -- tiny framework ------------------------------------------------------------------------
        private static void Check(bool ok, string what)
        {
            if (!ok)
                throw new Exception(what);
        }

        private static void Eq<T>(T actual, T expected, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(actual, expected))
                throw new Exception($"{what}: got {actual}, expected {expected}");
        }

        private static void Near(double actual, double expected, double tol, string what)
        {
            if (!(Math.Abs(actual - expected) <= tol))
                throw new Exception($"{what}: got {actual:R}, expected {expected:R} +- {tol}");
        }

        private static T Throws<T>(Action a, string what) where T : Exception
        {
            try
            {
                a();
            }
            catch (T e)
            {
                return e;
            }
            throw new Exception($"{what}: no {typeof(T).Name}");
        }

        private static void WaitFor(Func<bool> cond, double seconds, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!cond())
            {
                if (DateTime.UtcNow > until)
                    throw new Exception("timed out waiting for " + what);
                Thread.Sleep(5);
            }
        }

        private static byte[] Gz(string name)
        {
            using var fs = File.OpenRead(Path.Combine(_fix, name));
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            return ms.ToArray();
        }

        private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));
        private static string HexOf(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

        private static JsonElement Reference()
        {
            _reference ??= JsonDocument.Parse(File.ReadAllText(Path.Combine(_fix, "reference_values.json")));
            return _reference.RootElement;
        }

        private static JsonDocument? _reference;

        private static byte[] Window() => _window ??= Gz("tc001max_block_window.bin.gz");
        private static byte[]? _window;
        private static byte[] Block() => Window().AsSpan(Margin, Tc001Max.BlockBytes).ToArray();
        private static byte[] HeaderBytes() => File.ReadAllBytes(Path.Combine(_fix, "tc001max_header_window.bin")).AsSpan(1024, 12).ToArray();

        private static List<byte[]> FeedAll(BlockParser parser, byte[] data, int n)
        {
            var got = new List<byte[]>();
            for (var i = 0; i < data.Length; i += n)
                got.AddRange(parser.Feed(data.AsSpan(i, Math.Min(n, data.Length - i))));
            return got;
        }

        private static byte[] Cat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts)
                ms.Write(p);
            return ms.ToArray();
        }

        private static byte[] Fill(byte value, int n) => Enumerable.Repeat(value, n).ToArray();

        private static float[] Full(float v) => Enumerable.Repeat(v, Tc001Max.Pixels).ToArray();

        private static ushort[] RawOf(double c) => Enumerable.Repeat((ushort)Tc001Max.CelsiusToCounts(c), Tc001Max.Pixels).ToArray();

        private static byte[] Blocks(params int[] seqs) =>
            Cat(seqs.Select(s => Cat(Tc001Max.SyntheticBlock(Full(22f), s), Fill(0x11, 600))).ToArray());

        /// <summary>25 degC everywhere, 60 degC at sensor pixel (200, 40).</summary>
        private static float[] HotFrame()
        {
            var t = Full(25f);
            t[40 * 256 + 200] = 60f;
            return t;
        }

        private static Picture Live(float[] t, WebcamSettings? s = null, long settingsVersion = 0) => new()
        {
            Kind = PictureKind.Live,
            Version = 1,
            Temperature = t,
            Width = 256,
            Height = 192,
            Settings = s ?? WebcamSettings.Default,
            SettingsVersion = settingsVersion,
        };

        private static (byte R, byte G, byte B) PixelAt(byte[] bgra, int x, int y)
        {
            var o = (y * 640 + x) * 4;
            return (bgra[o + 2], bgra[o + 1], bgra[o]);
        }

        private static string TempDir()
        {
            var d = Path.Combine(Path.GetTempPath(), "tc001max-webcam-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static WebcamSettings Parse(string json, out List<string> problems)
        {
            problems = [];
            return SettingsFile.Parse(json, problems);
        }

        // -- denoiser (Core/Denoiser.cs) ------------------------------------------------------------
        private const int DW = Tc001Max.Width, DH = Tc001Max.Height, DN = DW * DH;

        private static float Gauss(Random r) =>
            (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()));

        private static float[] Noisy(float[] truth, Random r, float sigma)
        {
            var o = new float[DN];
            for (var i = 0; i < DN; i++)
                o[i] = truth[i] + sigma * Gauss(r);
            return o;
        }

        /// <summary>rms over the inner region (15 px border left out) of a - b, after removing the mean difference.</summary>
        private static double InnerRms(float[] a, float[] b)
        {
            double s = 0, s2 = 0;
            var n = 0;
            for (var y = 15; y < DH - 15; y++)
                for (var x = 15; x < DW - 15; x++)
                {
                    double d = a[y * DW + x] - b[y * DW + x];
                    s += d;
                    s2 += d * d;
                    n++;
                }
            return Math.Sqrt(Math.Max(s2 / n - (s / n) * (s / n), 0));
        }

        private static float[] Scene()
        {
            var t = new float[DN];
            for (var y = 0; y < DH; y++)
                for (var x = 0; x < DW; x++)
                    t[y * DW + x] = 24f + 2f * x / DW + 0.5f * y / DH;
            return t;
        }

        private static IEnumerable<(string, Action)> DenoiserTests()
        {
            yield return ("denoise: a still noisy scene gets much quieter", () =>
            {
                var r = new Random(1);
                var truth = Scene();
                var dn = new Denoiser(DW, DH);
                double rawErr = 0, outErr = 0;
                var n = 0;
                for (var k = 0; k < 300; k++)
                {
                    var x = Noisy(truth, r, Denoiser.SigmaC);
                    var o = dn.Process(x, k / 25.0);
                    if (k >= 100)
                    {
                        rawErr += InnerRms(x, truth);
                        outErr += InnerRms(o, truth);
                        n++;
                    }
                }
                rawErr /= n;
                outErr /= n;
                Check(outErr < 0.4 * rawErr, $"noise {rawErr * 1000:F1} -> {outErr * 1000:F1} mK");
            });
            yield return ("denoise: motion passes at once (no smearing)", () =>
            {
                var r = new Random(2);
                var truth = Scene();
                var dn = new Denoiser(DW, DH);
                float[] o = [];
                for (var k = 0; k < 120; k++)
                {
                    var tk = (float[])truth.Clone();
                    if (k >= 100)
                        for (var y = 60; y < 100; y++)
                            for (var x = 100; x < 140; x++)
                                tk[y * DW + x] += 2f;
                    o = dn.Process(Noisy(tk, r, Denoiser.SigmaC), k / 25.0);
                    if (k == 101)
                        Near(o[80 * DW + 120] - truth[80 * DW + 120], 2.0, 0.15, "a +2 K square, one frame after it appeared");
                }
                Near(o[80 * DW + 60] - truth[80 * DW + 60], 0.0, 0.1, "outside the square");
            });
            yield return ("denoise: a slow change of the whole scene is followed (flicker removal keeps it)", () =>
            {
                var r = new Random(3);
                var truth = Scene();
                var dn = new Denoiser(DW, DH);
                float[] o = [], tk = [];
                for (var k = 0; k < 750; k++)
                {
                    tk = (float[])truth.Clone();
                    var drift = 1.5f * k / 750f;                    // +1.5 K in 30 s
                    for (var i = 0; i < DN; i++)
                        tk[i] += drift;
                    o = dn.Process(Noisy(tk, r, Denoiser.SigmaC), k / 25.0);
                }
                double mo = 0, mt = 0;
                for (var i = 0; i < DN; i++)
                {
                    mo += o[i];
                    mt += tk[i];
                }
                Near((mo - mt) / DN, 0.0, 0.05, "mean level after 30 s of drift");
            });
            yield return ("denoise: shutter tables are averaged through their jumps", () =>
            {
                // each shutter: ~15 repeated frames, then a new table with its own 30 mK white error
                var r = new Random(4);
                var truth = Scene();
                var dn = new Denoiser(DW, DH);
                var table = new float[DN];
                double rawErr = 0, outErr = 0;
                var n = 0;
                var t = 0.0;
                float[] last = [];
                for (var shutter = 0; shutter <= 8; shutter++)
                {
                    if (shutter > 0)
                    {
                        for (var k = 0; k < 15; k++, t += 0.04)
                            dn.Process(last, t);             // the camera repeats its last frame
                        table = new float[DN];
                        for (var i = 0; i < DN; i++)
                            table[i] = 0.03f * Gauss(r);
                    }
                    var withTable = new float[DN];
                    for (var i = 0; i < DN; i++)
                        withTable[i] = truth[i] + table[i];
                    for (var k = 0; k < 250; k++, t += 0.04)
                    {
                        last = Noisy(withTable, r, Denoiser.SigmaC);
                        var o = dn.Process(last, t);
                        if (shutter >= 5 && k >= 150)
                        {
                            rawErr += InnerRms(withTable, truth);   // what the camera's table alone leaves
                            outErr += InnerRms(o, truth);
                            n++;
                        }
                    }
                }
                Eq(dn.Shutters, 8, "shutters seen");
                Eq((dn.JumpsUsed, dn.JumpsRejected), (8, 0), "jumps used / rejected");
                rawErr /= n;
                outErr /= n;
                Check(outErr < 0.75 * rawErr, $"table error {rawErr * 1000:F1} -> {outErr * 1000:F1} mK");
            });
            yield return ("denoise: a shutter while the scene moves clears the table history", () =>
            {
                var r = new Random(5);
                var truth = Scene();
                var dn = new Denoiser(DW, DH);
                var t = 0.0;
                float[] last = [];
                for (var k = 0; k < 60; k++, t += 0.04)
                {
                    last = Noisy(truth, r, Denoiser.SigmaC);
                    dn.Process(last, t);
                }
                for (var k = 0; k < 15; k++, t += 0.04)
                    dn.Process(last, t);
                var moved = (float[])truth.Clone();
                for (var y = 40; y < 140; y++)
                    for (var x = 60; x < 120; x++)
                        moved[y * DW + x] += 6f;                         // someone walked in during the shutter
                for (var k = 0; k < 40; k++, t += 0.04)
                    dn.Process(Noisy(moved, r, Denoiser.SigmaC), t);
                Eq((dn.Shutters, dn.JumpsUsed, dn.JumpsRejected), (1, 0, 1), "rejected");
                Check(dn.Correction.ToArray().All(v => v == 0), "no correction applied");
            });
            yield return ("denoise: no-reading pixels stay NaN; a 2-s gap starts afresh", () =>
            {
                var r = new Random(6);
                var truth = Scene();
                var dn = new Denoiser(DW, DH);
                float[] o = [];
                for (var k = 0; k < 50; k++)
                {
                    var x = Noisy(truth, r, Denoiser.SigmaC);
                    x[5] = float.NaN;
                    o = dn.Process(x, k / 25.0);
                }
                Check(float.IsNaN(o[5]) && !float.IsNaN(o[6]), "NaN passes, neighbours fine");
                var jump = (float[])truth.Clone();
                for (var i = 0; i < DN; i++)
                    jump[i] += 0.1f;                                     // smaller than the motion threshold
                o = dn.Process(jump, 50 / 25.0 + 3.0);
                Near(o[1000], jump[1000], 1e-5, "after a gap the first frame is passed as is");
                Check(Same(dn.Process(jump, 50 / 25.0 + 3.04), dn.Process(jump, 50 / 25.0 + 3.08)), "a repeated frame returns the same output");
            });
        }

        private static bool Same(float[] a, float[] b) => ReferenceEquals(a, b) || a.AsSpan().SequenceEqual(b);

        // -- the tests -----------------------------------------------------------------------------
        private static IEnumerable<(string, Action)> Tests()
        {
            // commands
            yield return ("commands match the captured bytes", () =>
            {
                Eq(HexOf(Tc001Max.CmdReadModel), "0101810001000000000000001e0000004f90", "read_model");
                Eq(HexOf(Tc001Max.CmdReadFirmware), "0101810002000000000000000c0000001f63", "read_firmware");
                Eq(HexOf(Tc001Max.CmdReadSerial), "01018100070000000000000040000000104c", "read_serial");
                Eq(HexOf(Tc001Max.CmdStartStream), "012f81000000000000000000010000004930", "start_stream");
                var c = Reference().GetProperty("commands");
                Eq(HexOf(Tc001Max.CmdReadModel), c.GetProperty("read_model").GetString(), "reference read_model");
                Eq(HexOf(Tc001Max.CmdStartStream), c.GetProperty("start_stream").GetString(), "reference start_stream");
                Eq(HexOf(Tc001Max.CmdReadFirmware), c.GetProperty("read_firmware").GetString(), "reference read_firmware");
                Eq(HexOf(Tc001Max.CmdReadSerial), c.GetProperty("read_serial").GetString(), "reference read_serial");
            });
            yield return ("crc16 xmodem against the P3 vectors", () =>
            {
                foreach (var v in new[] { "0101810006000000000000004000000065 4f", "010181000a000000000000004000000019 59",
                                          "1021810000000000000000000200000095 d1", "012f4100010000000000000000000000 4939",
                                          "01364300000000000000000000000000 cd0b" })
                {
                    var b = Hex(v);
                    Eq(b.Length, 18, "vector length");
                    var crc = Tc001Max.Crc16Xmodem(b.AsSpan(0, 16));
                    Eq(crc, (ushort)(b[16] | (b[17] << 8)), v);
                }
                Eq(Tc001Max.Crc16Xmodem("123456789"u8), (ushort)0x31C3, "check value");
            });
            yield return ("auto-shutter and shutter commands match the bytes sent in experiments/LOG.md", () =>
            {
                Eq(HexOf(Tc001Max.CmdGetAutoShutter), "100281000100000000000000010000000d37", "GetAutoFFCStatus");
                Eq(HexOf(Tc001Max.CmdAutoShutterOff), "100241000000000000000000000000000d3e", "SetAutoFFCStatus 0");
                Eq(HexOf(Tc001Max.CmdAutoShutterOn), "10024100010000000000000000000000783d", "SetAutoFFCStatus 1");
                Eq(HexOf(Tc001Max.CmdShutter), "01364300000000000000000000000000cd0b", "P3_PROTOCOL.md shutter");
            });
            yield return ("build command layout", () =>
            {
                var c = Tc001Max.BuildCommand(0x01, 0x0081, 0x0007, 64);
                Check(c.Length == 18 && c[0] == 0x01 && c[1] == 0x01, "head");
                Eq(HexOf(Tc001Max.BuildCommand(0x01, 0x01, 0x0081, 0x0007, 64)), HexOf(c), "two-byte type form, same bytes");
                Check(c[12] == 64 && c[13] == 0 && c.AsSpan(6, 6).ToArray().All(x => x == 0), "length / zeros");
            });

            // headers
            yield return ("parse the real header and nothing else", () =>
            {
                var w = File.ReadAllBytes(Path.Combine(_fix, "tc001max_header_window.bin"));
                var h = Tc001Max.ParseHeader(w, 1024);
                Check(h is { Info: 0x8C, PresentationTs: 0 } hh && hh.FrameNumber < 2048, "header at 1024");
                var hits = Enumerable.Range(0, w.Length - 12).Where(i => Tc001Max.ParseHeader(w, i) != null).ToList();
                Check(hits.SequenceEqual([1024]), $"headers found at {string.Join(",", hits)}");
            });
            yield return ("parse header rejects", () =>
            {
                foreach (var bad in new[] { "0d8c00000000000000000100", "0c8000000000000000000100", "0c8c00000000000000000008", "0c8c" })
                    Check(Tc001Max.ParseHeader(Hex(bad)) == null, bad);
            });

            // reassembly
            yield return ("parser finds the real block in any chunking", () =>
            {
                foreach (var chunk in new[] { 1, 333, 512, 16384, 1_000_000 })
                {
                    var parser = new BlockParser();
                    var got = FeedAll(parser, Window(), chunk);
                    Check(got.Count == 1 && got[0].AsSpan().SequenceEqual(Block()), $"chunk {chunk}: {got.Count} blocks");
                    Check(parser.Stats.Blocks == 1 && parser.Stats.Rejected == 0, $"chunk {chunk} stats");
                }
            });
            yield return ("real block decodes to the room (and matches the reference)", () =>
            {
                var d = Tc001Max.DecodeBlock(Block());
                Eq(d.Sequence, 456, "sequence");
                Check(d.Raw.All(r => r % 4 == 0), "1/16 K steps");
                BlockStats(d, Reference().GetProperty("blocks").GetProperty("block_window"));
            });
            yield return ("real spliced block has its header removed (and matches the reference)", () =>
            {
                var parser = new BlockParser();
                var got = FeedAll(parser, Gz("tc001max_spliced_window.bin.gz"), 16384);
                Check(got.Count == 1 && parser.Stats.Spliced == 1, "one spliced block");
                var d = Tc001Max.DecodeBlock(got[0]);
                Check(d.Raw.All(r => r % 4 == 0), "no header words left");
                BlockStats(d, Reference().GetProperty("blocks").GetProperty("spliced_window"));
            });
            yield return ("inserted header is spliced out", () =>
            {
                foreach (var offset in new[] { 700, 50_000, 98_303, 120_001, 196_000 })
                {
                    var w = Window();
                    var at = Margin + offset;
                    var stream = Cat(w[..at], HeaderBytes(), w[at..]);
                    var parser = new BlockParser();
                    var got = FeedAll(parser, stream, 16384);
                    Check(got.Count == 1 && got[0].AsSpan().SequenceEqual(Block()), $"offset {offset}");
                    Eq(parser.Stats.Spliced, 1, $"offset {offset} spliced");
                }
            });
            yield return ("a header anywhere in the block never shifts it", () =>
            {
                // 1.3.4: a header in the last bytes of the end row made the parser take the block
                // up to 20 bytes early (the frame shifted sideways; its first words read ~240 degC)
                // every offset near the picture/temperature boundary, the metadata and end rows and
                // the start, and a stride through the rest
                var offsets = new SortedSet<int>();
                for (var o = 0; o <= 2048; o++) offsets.Add(o);
                for (var o = Tc001Max.PictureBytes - 1100; o <= Tc001Max.PictureBytes + 1100; o++) offsets.Add(o);
                for (var o = Tc001Max.RawBytes - 1100; o <= Tc001Max.BlockBytes; o++) offsets.Add(o);
                for (var o = 0; o <= Tc001Max.BlockBytes; o += 97) offsets.Add(o);
                var bad = new List<string>();
                foreach (var offset in offsets)
                {
                    var w = Window();
                    var at = Margin + offset;
                    var stream = Cat(w[..at], HeaderBytes(), w[at..]);
                    var got = FeedAll(new BlockParser(), stream, offset % 3 == 0 ? 16384 : offset % 3 == 1 ? 333 : 7);
                    if (got.Count > 1 || (got.Count == 1 && !got[0].AsSpan().SequenceEqual(Block())))
                        bad.Add($"{offset}({got.Count} blocks)");
                    else if (got.Count == 0 && offset > 0 && offset < Tc001Max.BlockBytes)
                        bad.Add($"{offset}(lost)");
                }
                Check(bad.Count == 0, $"{bad.Count} offsets give a wrong block: {string.Join(" ", bad.Take(40))}");
            });
            yield return ("two headers in one block are rejected", () =>
            {
                var w = Window();
                int a = Margin + 100_000, b = Margin + 150_000;
                var parser = new BlockParser();
                var got = FeedAll(parser, Cat(w[..a], HeaderBytes(), w[a..b], HeaderBytes(), w[b..]), 16384);
                Check(got.Count == 0 && parser.Stats.Rejected >= 1, "rejected");
            });
            yield return ("damaged picture is rejected", () =>
            {
                foreach (var where in new[] { 10_001, 60_001 })
                {
                    var w = (byte[])Window().Clone();
                    w[Margin + where] ^= 0x01;
                    var parser = new BlockParser();
                    Check(FeedAll(parser, w, 16384).Count == 0, $"{where}: nothing");
                    Eq(parser.Stats.Rejected, 1, $"{where}: rejected");
                }
            });
            yield return ("lost packet is rejected", () =>
            {
                var w = Window();
                var cut = Margin + 150_000;
                Check(FeedAll(new BlockParser(), Cat(w[..cut], w[(cut + 512)..]), 16384).Count == 0, "nothing");
            });
            yield return ("joined mid-block then resyncs", () =>
            {
                var w = Window();
                var parser = new BlockParser();
                var got = FeedAll(parser, Cat(w[(Margin + 50_000)..], Tc001Max.SyntheticBlock(Full(22f), 457), Fill(0x11, 600)), 4096);
                Check(got.Count == 1 && Tc001Max.BlockSequence(got[0]) == 457, "block 457");
                Eq(parser.Stats.Partial, 1, "partial");
            });
            yield return ("resync after garbage and lost counting", () =>
            {
                var blocks = Enumerable.Range(0, 4).Select(i => Tc001Max.SyntheticBlock(Full(21f + i), 10 + i)).ToArray();
                var garbage = new byte[300_000];
                new Random(3).NextBytes(garbage);
                var parser = new BlockParser();
                var got = FeedAll(parser, Cat(blocks[0], garbage, blocks[1], blocks[2][..100_000], blocks[3], Fill(0x11, 600)), 16384);
                var seqs = got.Select(b => Tc001Max.BlockSequence(b)).ToList();
                Check(seqs.SequenceEqual([10, 11, 13]), $"sequences {string.Join(",", seqs)}");
                Eq(parser.Stats.Lost, 1, "lost");
            });
            yield return ("endless end-row run is bounded", () =>
            {
                var parser = new BlockParser();
                var run = Cat(Enumerable.Repeat(BlockParser.EndRowPattern.ToArray(), 64).ToArray());
                for (var i = 0; i < 300; i++)
                    parser.Feed(run);
                Check(parser.BufferedLength < Tc001Max.BlockBytes + (1 << 20) + run.Length, $"buffer {parser.BufferedLength}");
                Eq(parser.Stats.Blocks, 0, "blocks");
            });
            yield return ("memory is bounded on a long stream", () =>
            {
                var parser = new BlockParser();
                var junk = Cat(Enumerable.Repeat(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), 64).ToArray());
                for (var i = 0; i < 2000; i++)
                    parser.Feed(junk);
                Check(parser.BufferedLength <= Tc001Max.BlockBytes + (1 << 20) + junk.Length, $"buffer {parser.BufferedLength}");
            });

            // conversion / validation
            yield return ("counts conversion", () =>
            {
                Near(Tc001Max.CountsToCelsius(Tc001Max.CelsiusToCounts(0.0)), 0.0, 0.01, "0 degC");
                Near(Tc001Max.CountsToCelsius(64 * 300), 300 - 273.15, 1e-9, "300 K");
                Eq(Tc001Max.CelsiusToCounts(25.0), (int)Math.Round((25.0 + 273.15) * 64), "25 degC");
            });
            yield return ("synthetic block round trip", () =>
            {
                var t = Full(23f);
                for (var y = 50; y < 60; y++)
                    for (var x = 100; x < 110; x++)
                        t[y * 256 + x] = 80f;
                var d = Tc001Max.DecodeBlock(Tc001Max.SyntheticBlock(t, 7));
                Eq(d.Sequence, 7, "sequence");
                Near(d.TemperatureC[55 * 256 + 105], 80.0, 0.07, "hot square");
                var sorted = d.TemperatureC.OrderBy(v => v).ToArray();
                Near(sorted[sorted.Length / 2], 23.0, 0.07, "median");
            });
            yield return ("picture leak: repaired from the row below, nothing else touched", () =>
            {
                // a room with a gentle gradient, so "the row below" is distinguishable
                var t = new float[Tc001Max.Pixels];
                for (var y = 0; y < 192; y++)
                    for (var x = 0; x < 256; x++)
                        t[y * 256 + x] = 22f + y * 0.05f + x * 0.01f;
                var clean = Tc001Max.DecodeBlock(Tc001Max.SyntheticBlock(t, 3));
                Eq(clean.LeakRepaired, 0, "clean frame");
                foreach (var len in new[] { 6, 70, 300 })
                {
                    var b = Tc001Max.SyntheticBlock(t, 3);
                    for (var i = 0; i < len; i++)
                    {
                        b[Tc001Max.PictureBytes + 2 * i] = (byte)(50 + i % 200);   // luma
                        b[Tc001Max.PictureBytes + 2 * i + 1] = 0x80;              // chroma
                    }
                    Near(Tc001Max.CountsToCelsius(0x8000 + 50), 239.6, 0.1, "a leaked word reads ~240 degC");
                    var d = Tc001Max.DecodeBlock(b);
                    Eq(d.LeakRepaired, len, $"{len}: repaired");
                    var max = d.TemperatureC.Max();
                    Check(max < 40f, $"{len}: hottest {max} degC after the repair");
                    for (var i = 0; i < len; i++)
                    {
                        var j = i + 256;
                        while (j < len) j += 256;
                        Eq(d.Raw[i], clean.Raw[j], $"{len}: word {i} from word {j}");
                    }
                    for (var i = len; i < Tc001Max.Pixels; i++)
                        Check(d.Raw[i] == clean.Raw[i], $"{len}: word {i} untouched");
                }
                // a real 240 degC object away from the first word stays
                var hot = Full(23f);
                for (var y = 0; y < 10; y++)
                    for (var x = 100; x < 110; x++)
                        hot[y * 256 + x] = 240f;
                var h = Tc001Max.DecodeBlock(Tc001Max.SyntheticBlock(hot));
                Eq(h.LeakRepaired, 0, "real hot object");
                Near(h.TemperatureC[5 * 256 + 105], 240.0, 0.07, "real hot object kept");
                // more than half the frame is no measurement
                var all = Tc001Max.SyntheticBlock(Full(23f));
                for (var i = 0; i < Tc001Max.Pixels / 2 + 1; i++)
                    all[Tc001Max.PictureBytes + 2 * i + 1] = 0x80;
                Throws<FrameRejectedException>(() => Tc001Max.DecodeBlock(all), "picture data");
            });
            yield return ("picture leak: the auto range does not jump to 240 degC", () =>
            {
                var sm = new RangeSmoother();
                var t = Full(25f);
                for (var y = 80; y < 90; y++)
                    for (var x = 120; x < 130; x++)
                        t[y * 256 + x] = 33f;
                var good = Tc001Max.SyntheticBlock(t);
                var bad = (byte[])good.Clone();
                for (var i = 0; i < 40; i++)
                    bad[Tc001Max.PictureBytes + 2 * i + 1] = 0x80;
                var before = sm.Update(Tc001Max.DecodeBlock(good).TemperatureC, 256, 192, 0.00);
                var during = sm.Update(Tc001Max.DecodeBlock(bad).TemperatureC, 256, 192, 0.04);
                Eq(during, before, "range during the leaked frame");
                Check(before.Hi < 40, $"range {before}");
            });
            // updates (Core/UpdateRules.cs; the Windows side is src/TC001MaxWebcam/Updater.cs)
            yield return ("update: versions, the newest package of this product, newer or not", () =>
            {
                Eq(UpdateRules.ParseVersion("1.2.0"), new Version(1, 2, 0), "1.2.0");
                Eq(UpdateRules.ParseVersion(" 1.2 "), new Version(1, 2, 0), "1.2 is 1.2.0");
                Eq(UpdateRules.ParseVersion("1.2.3.4"), new Version(1, 2, 3), "a fourth field does not count");
                Eq(UpdateRules.ParseVersion("x"), null, "not a version");
                var code = UpdateRules.UpgradeCode;
                var msis = new[]
                {
                    new MsiInfo(@"d\TC001MaxWebcam-1.1.1-x64.msi", code, "1.1.1"),
                    new MsiInfo(@"d\TC001MaxWebcam-1.10.0-x64.msi", code.ToLowerInvariant(), "1.10.0"),
                    new MsiInfo(@"d\TC001MaxWebcam-1.2.0-x64.msi", code, "1.2.0"),
                    new MsiInfo(@"d\TC001MaxWebcam-9.0.0-x64.msi", "{00000000-0000-0000-0000-000000000000}", "9.0.0"),
                    new MsiInfo(@"d\TC001MaxWebcam-broken.msi", null, null),
                };
                var n = UpdateRules.Newest(msis);
                Check(n is { } nn && nn.Version == new Version(1, 10, 0) && nn.Msi.Path.Contains("1.10.0"), $"newest {n}");
                Eq(UpdateRules.Newest(msis.Where(m => m.ProductVersion != "1.10.0")) is { } m2 ? m2.Version : null, new Version(1, 2, 0), "without 1.10");
                Eq(UpdateRules.Newest([msis[3], msis[4]]) == null, true, "another product's package never counts");
                Check(UpdateRules.IsNewer(new Version(1, 2, 0), new Version(1, 1, 1)), "1.2.0 > 1.1.1");
                Check(!UpdateRules.IsNewer(new Version(1, 2, 0), new Version(1, 2, 0)), "same version");
                Check(!UpdateRules.IsNewer(new Version(1, 1, 0), new Version(1, 2, 0)), "older");
                Check(UpdateRules.IsNewer(new Version(1, 0, 0), null), "not installed");
            });
            yield return ("update: where to look (--from, remembered, installed from; never the MSI cache)", () =>
            {
                var win = @"C:\Windows";
                Eq(UpdateRules.Source(@"D:\b", @"E:\r", @"F:\i\x.msi", win), (@"D:\b", "--from"), "--from first");
                Eq(UpdateRules.Source(null, @"E:\r", @"F:\i\x.msi", win)?.Path, @"E:\r", "then remembered");
                Eq(UpdateRules.Source(" ", null, @"F:\i\x.msi", win)?.Path, @"F:\i", "then the installed .msi's folder");
                Eq(UpdateRules.Source(null, null, @"C:\Windows\Installer\1a2b3c.msi", win), null, "a repair from the cache says nothing");
                Eq(UpdateRules.Source(null, null, @"c:\windows\installer\sub\1a2b3c.msi", win), null, "cache, any case");
                Eq(UpdateRules.Source(null, null, @"C:\Windows\InstallerX\a.msi", win)?.Path, @"C:\Windows\InstallerX", "a sibling folder is fine");
                Eq(UpdateRules.Source(null, null, null, win), null, "nothing known");
            });
            yield return ("update: an app streaming, judged from webcam.log", () =>
            {
                const string start = "2026-11-21 14:24:40.835 [40592] info  stream started: opening the TC001 Max";
                const string stop = "2026-11-21 14:25:09.688 [40592] info  last stream stopped: closing the camera";
                const string other = "2026-11-21 14:24:40.843 [40592] warn  camera error, reconnecting: busy";
                Func<int, bool> alive = pid => pid == 40592;
                Check(!UpdateRules.StreamingByLog([], alive), "no log lines");
                Check(UpdateRules.StreamingByLog([start, other], alive), "started, not stopped");
                Check(!UpdateRules.StreamingByLog([start, other, stop], alive), "stopped");
                Check(UpdateRules.StreamingByLog([start, stop, start], alive), "started again");
                Check(!UpdateRules.StreamingByLog([start], _ => false), "the Frame Server that wrote it is gone");
                Check(UpdateRules.StreamingByLog(["no stamp stream started: x"], _ => false), "no pid: assume streaming");
            });
            yield return ("update: what msiexec's exit code means", () =>
            {
                Check(UpdateRules.DescribeMsiExit(0).Installed, "0");
                Check(UpdateRules.DescribeMsiExit(3010) is { Installed: true } r && r.Text.Contains("restart"), "3010");
                Check(!UpdateRules.DescribeMsiExit(1602).Installed, "1602 cancelled");
                Check(UpdateRules.DescribeMsiExit(1603) is { Installed: false } f && f.Text.Contains("1603"), "1603");
            });
            yield return ("validation accepts a room and rejects the rest", () =>
            {
                Tc001Max.ValidateTemperatures(RawOf(21.0));
                var cases = new (string Match, Action<ushort[]> Spoil)[]
                {
                    ("< -100", r => r[3 * 256 + 3] = 5000),
                    ("> 600", r => r[3 * 256 + 3] = (ushort)Tc001Max.CelsiusToCounts(682.5)),
                    ("median", r => Array.Fill(r, (ushort)Tc001Max.CelsiusToCounts(240.0))),
                    ("have a reading", r => Array.Fill(r, (ushort)0, 0, 150 * 256)),
                };
                foreach (var (match, spoil) in cases)
                {
                    var raw = RawOf(21.0);
                    spoil(raw);
                    var e = Throws<FrameRejectedException>(() => Tc001Max.ValidateTemperatures(raw), match);
                    Check(e.Message.Contains(match), $"'{e.Message}' should contain '{match}'");
                }
            });
            yield return ("decode rejects wrong length and a missing end row", () =>
            {
                var b = Block();
                Throws<FrameRejectedException>(() => Tc001Max.DecodeBlock(b[..^2]), "short");
                var e = Throws<FrameRejectedException>(() => Tc001Max.DecodeBlock(Cat(b[..^2], [0, 0])), "end row");
                Check(e.Message.Contains("end row"), e.Message);
            });
            yield return ("skip_first drops only the first block", () =>
            {
                var parser = new BlockParser(skipFirst: true);
                var got = FeedAll(parser, Blocks(1, 60, 61), 16384);
                Check(got.Select(b => Tc001Max.BlockSequence(b)).SequenceEqual([60, 61]), "60, 61");
                Eq(parser.Stats.Lost, 0, "lost");
            });

            // reference values (computed by the original Python code)
            yield return ("inferno LUT matches matplotlib (reference values)", () =>
            {
                var lut = Reference().GetProperty("inferno").EnumerateArray().ToList();
                Eq(lut.Count, 256, "entries");
                for (var i = 0; i < 256; i++)
                {
                    var rgb = lut[i].EnumerateArray().Select(v => v.GetInt32()).ToArray();
                    Check(InfernoLut.Rgb[3 * i] == rgb[0] && InfernoLut.Rgb[3 * i + 1] == rgb[1] && InfernoLut.Rgb[3 * i + 2] == rgb[2], $"entry {i}");
                }
            });
            yield return ("orientation matches the reference", () =>
            {
                var o = Reference().GetProperty("orient");
                var input = o.GetProperty("input").EnumerateArray().SelectMany(r => r.EnumerateArray().Select(v => v.GetInt32())).ToArray();
                foreach (var c in o.GetProperty("cases").EnumerateArray())
                {
                    var ori = new Orientation(c.GetProperty("rotate").GetInt32(), c.GetProperty("flip_h").GetBoolean(), c.GetProperty("flip_v").GetBoolean());
                    var name = $"rotate {ori.Rotate} h {ori.FlipH} v {ori.FlipV}";
                    Eq(ori.Code, c.GetProperty("code").GetUInt32(), name + " code");
                    Eq(Orientation.FromCode(ori.Code), ori, name + " from code");
                    var shown = ori.Apply(input, 3, 2, out var dw, out var dh);
                    var rows = c.GetProperty("shown").EnumerateArray().Select(r => r.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
                    Eq(dh, rows.Length, name + " height");
                    Eq(dw, rows[0].Length, name + " width");
                    Check(shown.SequenceEqual(rows.SelectMany(r => r)), name + " pixels");
                    foreach (var pt in c.GetProperty("points").EnumerateArray())
                    {
                        var v = pt.EnumerateArray().Select(e => e.GetInt32()).ToArray();
                        Eq(ori.ToDisplay(v[0], v[1], 3, 2), (v[2], v[3]), $"{name} point ({v[0]}, {v[1]})");
                    }
                }
            });
            yield return ("1st percentile matches numpy (float32 interpolation)", () =>
            {
                foreach (var c in Reference().GetProperty("percentile").EnumerateArray())
                {
                    var values = c.GetProperty("values").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
                    // numpy's float32 lerp is not reproducible to the last bit; a 1e-4 degC difference
                    // cannot move a whole-degree range (the smoother test checks that exactly).
                    Near(Stats.Percentile1(values, values.Length), c.GetProperty("p1").GetDouble(), 1e-4, $"n = {values.Length}");
                }
            });
            yield return ("auto range matches the reference RangeSmoother exactly", () =>
            {
                var s = Reference().GetProperty("smoother");
                var sm = new RangeSmoother(s.GetProperty("tau_s").GetDouble(), s.GetProperty("hysteresis_c").GetDouble(), s.GetProperty("min_span_c").GetDouble());
                var n = 0;
                foreach (var st in s.GetProperty("steps").EnumerateArray())
                {
                    var hot = st.GetProperty("hot");
                    (int, int, float)? h = hot.ValueKind == JsonValueKind.Array
                        ? (hot[0].GetInt32(), hot[1].GetInt32(), (float)hot[2].GetDouble()) : null;
                    var frame = SmootherFrame(st.GetProperty("i").GetInt32(), h, st.GetProperty("noise").GetBoolean(), st.GetProperty("nan_row0").GetInt32());
                    var (lo, hi) = sm.Update(frame, 64, 48, st.GetProperty("t").GetDouble());
                    var shown = st.GetProperty("shown");
                    Eq((lo, hi), (shown[0].GetDouble(), shown[1].GetDouble()), $"step {n}");
                    n++;
                }
                Check(n > 300, "steps");
            });

            // settings
            yield return ("settings: defaults and the canonical file round trip", () =>
            {
                var d = WebcamSettings.Default;
                Eq((d.Rotate, d.MirrorHorizontal, d.MirrorVertical, d.Units, d.Palette, d.Range, d.RangeMin, d.RangeMax),
                   (0, false, false, TempUnits.C, "inferno", RangeMode.Auto, 20.0, 40.0), "defaults");
                Eq(d.CustomPalette.Length, 4, "default custom stops");
                var s = d with { Rotate = 270, MirrorVertical = true, Units = TempUnits.F, Palette = "custom", CustomPalette = ["#102030", "#ffffff"], Range = RangeMode.Manual, RangeMin = 60.5, RangeMax = 100 };
                var json = SettingsFile.ToJson(s);
                Check(!json.Contains('\r') && json.EndsWith("}\n"), "LF line ends");
                Check(json.Contains("\"range\": \"manual\"") && json.Contains("\"units\": \"F\"") && json.Contains("60.5"), json);
                var back = Parse(json, out var problems);
                Eq(problems.Count, 0, "no problems");
                Check(SettingsWatcher.Same(back, s), "round trip");
                Check(SettingsWatcher.Same(Parse(SettingsFile.ToJson(d), out _), d), "defaults round trip");
            });
            yield return ("settings: a hand-written file (comments, trailing comma, aliases)", () =>
            {
                var s = Parse("""
                    // my camera is mounted upside down
                    {
                      "rotate": -180,
                      "Mirror_Horizontal": "yes",
                      "units": "fahrenheit",
                      "palette": "Gray",
                      "custom_palette": "#FF0000, 00ff00",
                      "range": "lock",
                    }
                    """, out var problems);
                Eq(problems.Count, 0, "problems: " + string.Join("; ", problems));
                Eq((s.Rotate, s.MirrorHorizontal, s.Units, s.Palette, s.Range), (180, true, TempUnits.F, "grey", RangeMode.Locked), "values");
                Check(s.CustomPalette.SequenceEqual(["#ff0000", "#00ff00"]), "colours normalised");
                Eq((s.RangeMin, s.RangeMax), (68.0, 104.0), "default manual range in F");
            });
            yield return ("settings: bad values are reported and keep their defaults", () =>
            {
                var s = Parse("""
                    {"rotate": 45, "mirror_vertical": 3, "units": "K", "palette": "jet", "custom_palette": ["#000000"],
                     "range": "fixed", "range_min": 50, "range_max": 30, "zoom": 2, "mirror_horizontal": true}
                    """, out var problems);
                Eq((s.Rotate, s.MirrorVertical, s.Units, s.Palette, s.Range), (0, false, TempUnits.C, "inferno", RangeMode.Auto), "defaults kept");
                Check(s.MirrorHorizontal, "the good key still applies");
                Eq((s.RangeMin, s.RangeMax), (20.0, 40.0), "inverted range replaced");
                Eq(s.CustomPalette.Length, 4, "custom default");
                foreach (var word in new[] { "rotate", "mirror_vertical", "units", "palette", "custom_palette", "range must be", "range_min (50)", "zoom" })
                    Check(problems.Any(p => p.Contains(word)), $"a problem mentions '{word}': {string.Join(" | ", problems)}");
                Eq(problems.Count, 8, "problem count");
                var none = Parse("{\"range_min\": 1000}", out problems);
                Check(problems.Single().Contains("between -100 and 600"), problems.Single());
                Eq(none.RangeMin, 20.0, "out of limits");
                foreach (var junk in new[] { "", "{", "[1, 2]", "\"text\"" })
                {
                    Check(SettingsWatcher.Same(Parse(junk, out problems), WebcamSettings.Default), $"junk '{junk}': defaults");
                    Eq(problems.Count, 1, $"junk '{junk}': one problem");
                }
            });
            yield return ("settings set: validates every value and writes nothing on error", () =>
            {
                var d = WebcamSettings.Default;
                var s = SettingsFile.Apply(d, ["rotate=90", "mirror_horizontal=on", "palette=custom", "custom_palette=#000000,#ff0000,#ffff00,#ffffff", "range=manual", "range_min=18.5", "range_max=35"]);
                Eq((s.Rotate, s.MirrorHorizontal, s.Palette, s.Range, s.RangeMin, s.RangeMax), (90, true, "custom", RangeMode.Manual, 18.5, 35.0), "applied");
                Eq(s.CustomPalette.Length, 4, "stops");
                var e = Throws<SettingsException>(() => SettingsFile.Apply(d, ["rotate=45", "palette=jet", "nonsense", "zoom=2", "range_min=1,5"]), "bad");
                var lines = e.Message.Split('\n');
                Eq(lines.Length, 5, "every error listed: " + e.Message);
                Check(lines.Any(l => l.Contains("not key=value")) && lines.Any(l => l.Contains("unknown setting 'zoom'")), e.Message);
                Check(Throws<SettingsException>(() => SettingsFile.Apply(d, ["range_min=50"]), "min above max").Message.Contains("below range_max"), "cross check");
                Throws<SettingsException>(() => SettingsFile.Apply(d, []), "nothing to set");
                Throws<SettingsException>(() => SettingsFile.Apply(d, ["custom_palette=#000000,#111111,#222222,#333333,#444444,#555555"]), "6 colours");
                Throws<SettingsException>(() => SettingsFile.Apply(d, ["custom_palette=#000000"]), "1 colour");
                Throws<SettingsException>(() => SettingsFile.Apply(d, ["custom_palette=#000000,#12345"]), "short hex");
                Throws<SettingsException>(() => SettingsFile.Apply(d, ["range_max=NaN"]), "NaN");
                Throws<SettingsException>(() => SettingsFile.Apply(d, ["mirror_vertical=maybe"]), "bool");
            });
            yield return ("settings: auto_shutter on | off", () =>
            {
                var d = WebcamSettings.Default;
                Check(d.AutoShutter, "default on (the camera's own behaviour)");
                Check(SettingsFile.ToJson(d).Contains("\"auto_shutter\": \"on\""), "written as \"on\"");
                var off = SettingsFile.Apply(d, ["auto_shutter=off"]);
                Check(!off.AutoShutter, "set off");
                Check(SettingsFile.ToJson(off).Contains("\"auto_shutter\": \"off\""), "written as \"off\"");
                Check(SettingsWatcher.Same(Parse(SettingsFile.ToJson(off), out var problems), off) && problems.Count == 0, "round trip");
                Check(!SettingsWatcher.Same(off, d), "a change is a change (the watcher bumps the version)");
                Check(!Parse("{\"auto_shutter\": false}", out problems).AutoShutter && problems.Count == 0, "JSON false accepted");
                Check(Parse("{\"auto_shutter\": \"OFF\"}", out _).AutoShutter == false, "case-insensitive");
                var bad = Parse("{\"auto_shutter\": \"sometimes\"}", out problems);
                Check(bad.AutoShutter && problems.Single().Contains("auto_shutter must be on or off"), string.Join(" | ", problems));
                Check(Throws<SettingsException>(() => SettingsFile.Apply(d, ["auto_shutter=2"]), "bad value").Message.Contains("on or off"), "strict set");
                Check(SettingsWatcher.Describe(off).Contains("auto shutter off"), SettingsWatcher.Describe(off));
                Check(SettingsFile.Help.Any(h => h.StartsWith("auto_shutter=on|off")), "help line");
            });
            yield return ("settings: idle_auto_shutter off | on | unchanged", () =>
            {
                var d = WebcamSettings.Default;
                Eq(d.IdleAutoShutter, (bool?)false, "default off (labbench's idle_auto_shutter default)");
                Check(SettingsFile.ToJson(d).Contains("\"idle_auto_shutter\": \"off\""), "written as \"off\"");
                foreach (var (text, want) in new[] { ("on", (bool?)true), ("off", false), ("unchanged", null), ("KEEP", null) })
                {
                    var s = SettingsFile.Apply(d, [$"idle_auto_shutter={text}"]);
                    Eq(s.IdleAutoShutter, want, text);
                    Check(SettingsWatcher.Same(Parse(SettingsFile.ToJson(s), out var problems), s) && problems.Count == 0, $"round trip {text}");
                }
                var bad = Parse("{\"idle_auto_shutter\": \"later\"}", out var probs);
                Check(bad.IdleAutoShutter == false && probs.Single().Contains("off, on or unchanged"), string.Join(" | ", probs));
                Check(SettingsWatcher.Describe(d).EndsWith("idle off"), SettingsWatcher.Describe(d));
                Check(SettingsFile.Help.Any(h => h.StartsWith("idle_auto_shutter=")), "help line");
            });
            yield return ("settings: overlay, crosshair, hotspot, readouts, scale", () =>
            {
                var d = WebcamSettings.Default;
                Check(d.Overlay && d.Crosshair && d.HotSpot && d.Readouts && d.Scale, "all shown by default");
                var json = SettingsFile.ToJson(d);
                foreach (var k in new[] { "overlay", "crosshair", "hotspot", "readouts", "scale" })
                {
                    Check(json.Contains($"\"{k}\": true"), $"{k} written");
                    Check(SettingsFile.Help.Any(h => h.StartsWith(k + "=")), $"{k} help line");
                }
                var s = SettingsFile.Apply(d, ["overlay=off", "crosshair=false", "hotspot=no", "readouts=0", "scale=false"]);
                Check(!s.Overlay && !s.Crosshair && !s.HotSpot && !s.Readouts && !s.Scale, "all off");
                Check(SettingsWatcher.Same(Parse(SettingsFile.ToJson(s), out var problems), s) && problems.Count == 0, "round trip");
                Check(!SettingsWatcher.Same(s, d), "a change is a change");
                Check(SettingsWatcher.Describe(s).EndsWith("overlay off"), SettingsWatcher.Describe(s));
                Check(SettingsWatcher.Describe(d with { Crosshair = false, Scale = false }).EndsWith("hidden: crosshair scale"), "describe hidden");
                Check(!SettingsWatcher.Describe(d).Contains("hidden"), "describe defaults");
                var old = Parse("{\"rotate\": 90}", out problems);
                Check(old.Overlay && old.Scale && problems.Count == 0, "an older file shows everything");
                Throws<SettingsException>(() => SettingsFile.Apply(d, ["scale=maybe"]), "bool");
            });
            yield return ("settings: denoise", () =>
            {
                var d = WebcamSettings.Default;
                Check(!d.Denoise, "off by default");
                Check(SettingsFile.ToJson(d).Contains("\"denoise\": false"), "written");
                Check(SettingsFile.Help.Any(h => h.StartsWith("denoise=")), "help line");
                var s = SettingsFile.Apply(d, ["denoise=on"]);
                Check(s.Denoise && !SettingsWatcher.Same(s, d), "set");
                Check(SettingsWatcher.Same(Parse(SettingsFile.ToJson(s), out var problems), s) && problems.Count == 0, "round trip");
                Check(SettingsWatcher.Describe(s).EndsWith("denoise on"), SettingsWatcher.Describe(s));
                Check(!SettingsWatcher.Describe(d).Contains("denoise"), "describe defaults");
                Check(!Parse("{\"rotate\": 90}", out _).Denoise, "an older file: off");
                Throws<SettingsException>(() => SettingsFile.Apply(d, ["denoise=maybe"]), "bool");
            });
            foreach (var t in DenoiserTests())
                yield return t;
            yield return ("settings: range_speed, and the auto range narrows at that speed", () =>
            {
                var d = WebcamSettings.Default;
                Eq((d.RangeSpeed, d.RangeTauS), (RangeSpeed.Normal, 4.0), "default normal, 4 s");
                Check(SettingsFile.ToJson(d).Contains("\"range_speed\": \"normal\""), "written");
                Check(SettingsFile.Help.Any(h => h.StartsWith("range_speed=")), "help line");
                foreach (var (text, want, tau) in new[] { ("slow", RangeSpeed.Slow, 8.0), ("Fast", RangeSpeed.Fast, 1.0), ("instant", RangeSpeed.Instant, 0.0), ("normal", RangeSpeed.Normal, 4.0) })
                {
                    var st = SettingsFile.Apply(d, [$"range_speed={text}"]);
                    Eq((st.RangeSpeed, st.RangeTauS), (want, tau), text);
                    Check(SettingsWatcher.Same(Parse(SettingsFile.ToJson(st), out var problems), st) && problems.Count == 0, $"round trip {text}");
                }
                Throws<SettingsException>(() => SettingsFile.Apply(d, ["range_speed=warp"]), "bad value");
                Check(SettingsWatcher.Describe(d with { RangeSpeed = RangeSpeed.Fast }).Contains("range auto (fast)"), "describe");

                // 25 degC with a 60 degC spot, then 1 s later the spot is gone: the top of the range
                // falls from 60 by 1 - exp(-1 / tau) of the way to 25 (rounded up to whole degrees)
                foreach (var (tau, hi) in new[] { (8.0, 56.0), (4.0, 53.0), (1.0, 38.0), (0.0, 27.0) })
                {
                    var sm = new RangeSmoother { TauS = tau };
                    Eq(sm.Update(HotFrame(), 256, 192, 0.0), (25.0, 60.0), $"tau {tau}: first frame");
                    Eq(sm.Update(Full(25f), 256, 192, 1.0).Hi, hi, $"tau {tau}: 1 s later");
                }
                // the renderer takes the speed from the settings
                var r = new FrameRenderer();
                var fast = d with { RangeSpeed = RangeSpeed.Fast };
                var bgra = new byte[FrameRenderer.OutBytes];
                r.Render(new Picture { Kind = PictureKind.Live, Temperature = HotFrame(), Width = 256, Height = 192, Settings = fast, TWall = 100.0 }, bgra);
                r.Render(new Picture { Kind = PictureKind.Live, Temperature = Full(25f), Width = 256, Height = 192, Settings = fast, TWall = 101.0 }, bgra);
                Eq(r.LastRange.Hi, 38.0, "renderer: fast");
            });
            yield return ("settings set: units change converts the manual range", () =>
            {
                var c = WebcamSettings.Default with { Range = RangeMode.Manual, RangeMin = 20, RangeMax = 37 };
                var f = SettingsFile.Apply(c, ["units=F"]);
                Eq((f.Units, f.RangeMin, f.RangeMax), (TempUnits.F, 68.0, 98.6), "converted to F");
                var back = SettingsFile.Apply(f, ["units=c"]);
                Eq((back.Units, back.RangeMin, back.RangeMax), (TempUnits.C, 20.0, 37.0), "and back");
                var given = SettingsFile.Apply(c, ["range_max=212", "units=F", "range_min=32"]);
                Eq((given.RangeMin, given.RangeMax), (32.0, 212.0), "values given with the units are in the new units");
                Throws<SettingsException>(() => SettingsFile.Apply(c, ["units=F", "range_max=1200"]), "F limit 1112");
                Eq(SettingsFile.Apply(c, ["units=C"]).RangeMax, 37.0, "same units: unchanged");
            });
            yield return ("settings file: save, load (BOM tolerated), atomic replace", () =>
            {
                var dir = TempDir();
                try
                {
                    var path = Path.Combine(dir, "settings.json");
                    var problems = new List<string>();
                    Check(SettingsWatcher.Same(SettingsFile.Load(path, problems), WebcamSettings.Default) && problems.Count == 0, "missing file: defaults");
                    var s = WebcamSettings.Default with { Rotate = 180, Palette = "iron" };
                    SettingsFile.Save(path, s);
                    Check(File.ReadAllBytes(path)[0] == (byte)'{', "no BOM written");
                    Check(!File.Exists(path + ".tmp"), "temporary file gone");
                    Check(SettingsWatcher.Same(SettingsFile.Load(path, problems), s), "loaded");
                    File.WriteAllText(path, "{\"rotate\": 90}", new UTF8Encoding(true)); // as Notepad / PowerShell may write it
                    Eq(SettingsFile.Load(path, problems).Rotate, 90, "BOM");
                    Eq(problems.Count, 0, "no problems");
                    // a reader holding the file for a moment (the camera reading it) delays the replace, not fails it
                    var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var closer = new Thread(() => { Thread.Sleep(30); reader.Dispose(); });
                    closer.Start();
                    SettingsFile.Save(path, s);
                    closer.Join();
                    Eq(SettingsFile.Load(path, problems).Rotate, 180, "replaced after the reader let go");
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
            });
            yield return ("settings watcher: re-reads only when the file changes", () =>
            {
                var dir = TempDir();
                try
                {
                    var path = Path.Combine(dir, "settings.json");
                    var clock = new FakeClock();
                    var w = new SettingsWatcher(path, clock, pollS: 1.0);
                    Check(!w.Poll(force: true), "missing: no change");
                    Eq(w.Version, 0L, "version 0");
                    SettingsFile.Save(path, WebcamSettings.Default with { Rotate = 90 });
                    Check(!w.Poll(), "within the poll interval: not read yet");
                    clock.Advance(1.0);
                    Check(w.Poll(), "changed");
                    Eq((w.Current.Rotate, w.Version), (90, 1L), "read");
                    clock.Advance(1.0);
                    Check(!w.Poll(), "unchanged file: no change");
                    SettingsFile.Save(path, WebcamSettings.Default with { Rotate = 90 });
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10));
                    clock.Advance(1.0);
                    Check(!w.Poll(), "rewritten with the same values: same version");
                    Eq(w.Version, 1L, "version kept");
                    File.WriteAllText(path, "{\"rotate\": 45, \"units\": \"F\"}");
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(20));
                    Check(w.Poll(force: true), "force reads now");
                    Eq((w.Current.Rotate, w.Current.Units, w.Problems.Count), (0, TempUnits.F, 1), "lenient with a problem");
                    File.Delete(path);
                    Check(w.Poll(force: true) && SettingsWatcher.Same(w.Current, WebcamSettings.Default), "deleted: defaults");
                    Check(!new SettingsWatcher(null, clock).Poll(force: true), "no path: defaults only");
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
            });

            // palettes
            yield return ("palettes: presets, inferno and colour parsing", () =>
            {
                foreach (var name in Palettes.Names)
                {
                    var lut = Palettes.Lut(WebcamSettings.Default with { Palette = name });
                    Eq(lut.Length, 768, name + " size");
                }
                Check(ReferenceEquals(Palettes.Lut(WebcamSettings.Default), InfernoLut.Rgb), "inferno is matplotlib's table");
                foreach (var (name, stops) in Palettes.Presets)
                {
                    var lut = Palettes.Lut(WebcamSettings.Default with { Palette = name });
                    Eq((lut[0], lut[1], lut[2]), Palettes.ParseHex(stops[0]), name + " coldest = first stop");
                    Eq((lut[765], lut[766], lut[767]), Palettes.ParseHex(stops[^1]), name + " hottest = last stop");
                }
                var grey = Palettes.Lut(WebcamSettings.Default with { Palette = "grey" });
                for (var i = 0; i < 256; i++)
                    Check(grey[3 * i] == i && grey[3 * i + 1] == i && grey[3 * i + 2] == i, $"grey {i}");
                Eq(Palettes.ParseHex("#FF8800"), ((byte)255, (byte)136, (byte)0), "upper case");
                Eq(Palettes.ParseHex(" 0a0b0c "), ((byte)10, (byte)11, (byte)12), "no #");
                Eq(Palettes.NormaliseHex("ABCDEF"), "#abcdef", "normalised");
                foreach (var bad in new[] { "#ff880", "#gg8800", "ff88001", "", "#", "red", "+12345" })
                    Throws<SettingsException>(() => Palettes.ParseHex(bad), $"'{bad}'");
            });
            yield return ("palettes: labbench's set (iron, turbo rainbow, white / black hot, names)", () =>
            {
                Eq(string.Join(",", Palettes.Names), "inferno,iron,rainbow,grey,black_hot,custom", "names");
                Eq(string.Join(",", Palettes.Presets["iron"]), "#000000,#20008c,#9b009b,#e1301e,#ff9600,#ffe650,#ffffff", "labbench's iron");
                Check(ReferenceEquals(Palettes.Lut(WebcamSettings.Default with { Palette = "rainbow" }), TurboLut.Rgb), "rainbow is turbo");
                Eq(TurboLut.Rgb.Length, 768, "turbo size");
                Eq((TurboLut.Rgb[0], TurboLut.Rgb[1], TurboLut.Rgb[2]), ((byte)48, (byte)18, (byte)59), "turbo 0 (matplotlib, truncated)");
                Eq((TurboLut.Rgb[384], TurboLut.Rgb[385], TurboLut.Rgb[386]), ((byte)164, (byte)252, (byte)59), "turbo 128");
                Eq((TurboLut.Rgb[765], TurboLut.Rgb[766], TurboLut.Rgb[767]), ((byte)122, (byte)4, (byte)2), "turbo 255");
                var black = Palettes.Lut(WebcamSettings.Default with { Palette = "black_hot" });
                for (var i = 0; i < 256; i++)
                    Check(black[3 * i] == 255 - i && black[3 * i + 2] == 255 - i, $"black hot {i}");
                foreach (var (typed, want) in new[] { ("white_hot", "grey"), ("White hot", "grey"), ("GRAY", "grey"), ("black-hot", "black_hot"),
                                                      ("turbo", "rainbow"), ("Iron", "iron"), ("custom", "custom") })
                    Eq(SettingsFile.Apply(WebcamSettings.Default, [$"palette={typed}"]).Palette, want, typed);
                Throws<SettingsException>(() => SettingsFile.Apply(WebcamSettings.Default, ["palette=jet"]), "unknown");
                Eq(Palettes.Label("grey") + "|" + Palettes.Label("black_hot"), "White hot|Black hot", "labels");
                Eq(string.Join(",", WebcamSettings.Default.CustomPalette), "#000040,#00a0ff,#ffff00,#ff0000", "labbench's default custom");
                var both = SettingsFile.Apply(WebcamSettings.Default, ["palette=custom", "custom_palette=#112233,#AABBCC,#ffffff"]);
                Eq((both.Palette, string.Join(",", both.CustomPalette)), ("custom", "#112233,#aabbcc,#ffffff"), "the tray sets both at once");
            });
            yield return ("palettes: custom stops are spread evenly and interpolated", () =>
            {
                var two = Palettes.Lut(WebcamSettings.Default with { Palette = "custom", CustomPalette = ["#000000", "#ffffff"] });
                for (var i = 0; i < 256; i++)
                    Check(two[3 * i] == i && two[3 * i + 2] == i, $"two stops: {i}");
                var three = Palettes.Lut(WebcamSettings.Default with { Palette = "custom", CustomPalette = ["#000000", "#ff0000", "#0000ff"] });
                Eq((three[0], three[1], three[2]), ((byte)0, (byte)0, (byte)0), "0: first stop");
                Eq((three[3 * 127], three[3 * 127 + 1], three[3 * 127 + 2]), ((byte)254, (byte)0, (byte)0), "127: just below the red stop");
                Eq((three[3 * 128], three[3 * 128 + 1], three[3 * 128 + 2]), ((byte)254, (byte)0, (byte)1), "128: just past it");
                Eq((three[3 * 64], three[3 * 64 + 1], three[3 * 64 + 2]), ((byte)128, (byte)0, (byte)0), "64: half way to red (127.5 rounds up)");
                Eq((three[765], three[766], three[767]), ((byte)0, (byte)0, (byte)255), "255: last stop");
                var five = Palettes.FromStops([(0, 0, 0), (100, 0, 0), (100, 100, 0), (100, 100, 100), (200, 200, 200)]);
                // index i sits at position i * 4 / 255 between the stops
                Eq((five[3 * 51], five[3 * 51 + 1]), ((byte)80, (byte)0), "51: 0.8 of the way to stop 1");
                Eq((five[3 * 255], five[3 * 255 + 1], five[3 * 255 + 2]), ((byte)200, (byte)200, (byte)200), "255: last of five");
                Throws<ArgumentException>(() => Palettes.FromStops([(1, 2, 3)]), "one stop");
            });

            // units
            yield return ("units: Fahrenheit conversion", () =>
            {
                Eq(Temperature.ToUnits(25f, TempUnits.F), 77f, "25 C");
                Eq(Temperature.ToUnits(-40f, TempUnits.F), -40f, "-40");
                Eq(Temperature.ToUnits(100.0, TempUnits.F), 212.0, "100 C");
                Eq(Temperature.ToUnits(37.0, TempUnits.C), 37.0, "C unchanged");
                Near(Temperature.ToCelsius(98.6, TempUnits.F), 37.0, 1e-9, "back");
                float[]? scratch = null;
                var src = new[] { 0f, float.NaN, 60f };
                Check(ReferenceEquals(Temperature.FrameToUnits(src, TempUnits.C, ref scratch), src), "C: the same array");
                var f = Temperature.FrameToUnits(src, TempUnits.F, ref scratch);
                Check(f[0] == 32f && float.IsNaN(f[1]) && f[2] == 140f && src[2] == 60f, "F frame, source untouched");
                Eq(WebcamSettings.Default.UnitSymbol + (WebcamSettings.Default with { Units = TempUnits.F }).UnitSymbol, "°C°F", "symbols");
            });
            yield return ("renderer: Fahrenheit range and manual range in F", () =>
            {
                var r = new FrameRenderer();
                var bgra = new byte[FrameRenderer.OutBytes];
                r.Render(Live(HotFrame(), WebcamSettings.Default with { Units = TempUnits.F }), bgra);
                Eq(r.LastRange, (77.0, 140.0), "auto range in F");
                var manual = WebcamSettings.Default with { Units = TempUnits.F, Palette = "grey", Range = RangeMode.Manual, RangeMin = 70, RangeMax = 80 };
                r.Render(Live(HotFrame(), manual, 1), bgra);
                Eq(r.LastRange, (70.0, 80.0), "manual range");
                Eq(PixelAt(bgra, 100, 240), ((byte)178, (byte)178, (byte)178), "77 F on a 70..80 grey scale");
            });

            // picture
            yield return ("renderer: live picture, hot spot, range", () =>
            {
                var r = new FrameRenderer();
                var bgra = new byte[FrameRenderer.OutBytes];
                r.Render(new Picture { Kind = PictureKind.Live, Version = 1, Temperature = HotFrame(), Width = 256, Height = 192, TWall = 1.8e9, Source = "test" }, bgra);
                Eq(r.LastImageRect, (0, 0, 640, 480), "fills the frame");
                Eq(r.LastRange, (25.0, 60.0), "range");
                Eq(r.LastHotSpotPx, ((int, int)?)(501, 101), "hot spot");
                Eq(PixelAt(bgra, 501, 101), ((byte)0, (byte)255, (byte)96), "green marker");
            });
            yield return ("renderer: overlay switches hide the marks and bars, not the badge", () =>
            {
                var grey = WebcamSettings.Default with { Palette = "grey", Range = RangeMode.Manual, RangeMin = 20, RangeMax = 30 };
                var plain = ((byte)127, (byte)127, (byte)127); // 25 degC on a 20..30 grey scale
                (FrameRenderer R, byte[] Px) Draw(WebcamSettings st, string? badge = null)
                {
                    var r = new FrameRenderer();
                    var bgra = new byte[FrameRenderer.OutBytes];
                    r.Render(new Picture { Kind = PictureKind.Live, Version = 1, Temperature = HotFrame(), Width = 256, Height = 192, Settings = st, TWall = 1.8e9, Badge = badge }, bgra);
                    return (r, bgra);
                }
                var (on, all) = Draw(grey);
                Check(PixelAt(all, 4, 4) != plain, "readouts bar drawn");
                Check(PixelAt(all, 4, 470) != plain, "scale bar drawn");
                Eq(PixelAt(all, 321, 241), ((byte)255, (byte)255, (byte)255), "centre x drawn");
                Check(on.LastHotSpotPx != null, "hot spot drawn");

                var (offR, off) = Draw(grey with { Overlay = false });
                Eq(offR.LastHotSpotPx, ((int, int)?)null, "overlay off: no hot spot");
                foreach (var (x, y) in new[] { (4, 4), (4, 470), (321, 241), (100, 240) })
                    Eq(PixelAt(off, x, y), plain, $"overlay off: bare picture at ({x}, {y})");
                Eq(PixelAt(off, 501, 101), ((byte)255, (byte)255, (byte)255), "overlay off: the hot pixel itself still shows");

                Eq(PixelAt(Draw(grey with { Readouts = false }).Px, 4, 4), plain, "readouts off");
                Check(PixelAt(Draw(grey with { Readouts = false }).Px, 4, 470) != plain, "readouts off: scale stays");
                Eq(PixelAt(Draw(grey with { Scale = false }).Px, 4, 470), plain, "scale off");
                Check(PixelAt(Draw(grey with { Scale = false }).Px, 4, 4) != plain, "scale off: readouts stay");
                Eq(PixelAt(Draw(grey with { Crosshair = false }).Px, 321, 241), plain, "crosshair off");
                Eq(Draw(grey with { HotSpot = false }).R.LastHotSpotPx, ((int, int)?)null, "hotspot off");
                Check(Draw(grey with { Crosshair = false }).R.LastHotSpotPx != null, "crosshair off: hot spot stays");

                var badged = Draw(grey with { Overlay = false }, "Calibrating (shutter)").Px;
                Check(PixelAt(badged, 320, 240) != plain, "overlay off: the badge still shows");
            });
            yield return ("tray art: icon in the palette's colours, green cross, round corners", () =>
            {
                foreach (var n in new[] { 16, 20, 24, 32, 48 })
                {
                    var px = TrayArt.Icon(n, InfernoLut.Rgb);
                    Eq(px.Length, n * n * 4, $"{n}: size");
                    Check(px[3] < 32, $"{n}: corner (nearly) transparent, alpha {px[3]}"); // anti-aliased edge
                    Eq(px[((n / 2) * n + n / 2) * 4 + 3], (byte)255, $"{n}: centre opaque");
                    var cx = (int)(0.47 * n);
                    var cy = (int)(0.62 * n);
                    var o = (cy * n + cx) * 4;
                    Check(px[o + 1] > 200 && px[o + 2] < 60, $"{n}: green cross at its centre ({px[o + 2]}, {px[o + 1]}, {px[o]})");
                    for (var i = 0; i < px.Length; i += 4)
                        Check(px[i] <= px[i + 3] && px[i + 1] <= px[i + 3] && px[i + 2] <= px[i + 3], $"{n}: premultiplied");
                }
                var grey = TrayArt.Icon(32, Palettes.Lut(WebcamSettings.Default with { Palette = "grey" }));
                var inferno = TrayArt.Icon(32, InfernoLut.Rgb);
                Check(!grey.SequenceEqual(inferno), "follows the palette");
                var sw = TrayArt.Swatch(30, 10, InfernoLut.Rgb);
                Eq(sw.Length, 30 * 10 * 4, "swatch size");
                var mid = (5 * 30 + 28) * 4;
                var li = 28 * 255 / 29 * 3; // column 28 of 0..29
                Eq((sw[mid + 2], sw[mid + 1], sw[mid]), (InfernoLut.Rgb[li], InfernoLut.Rgb[li + 1], InfernoLut.Rgb[li + 2]), "swatch: hot on the right");
            });
            yield return ("renderer: rotated picture is letterboxed", () =>
            {
                var r = new FrameRenderer();
                var bgra = new byte[FrameRenderer.OutBytes];
                r.Render(new Picture
                {
                    Kind = PictureKind.Live, Temperature = HotFrame(), Width = 256, Height = 192,
                    Settings = WebcamSettings.Default with { Rotate = 90 }, Badge = "Calibrating (shutter)", Dim = true,
                }, bgra);
                Eq(r.LastImageRect, (140, 0, 360, 480), "letterbox");
                Eq(r.LastHotSpotPx, ((int, int)?)(424, 375), "hot spot rotated");
                Eq(PixelAt(bgra, 10, 240), ((byte)0, (byte)0, (byte)0), "black border");
            });
            yield return ("mirror / rotate settings map the picture as described", () =>
            {
                Eq(new WebcamSettings { Rotate = 270, MirrorHorizontal = true }.Orientation, new Orientation(270, true, false), "settings -> orientation");
                Eq(new Orientation(90, false, false).ToDisplay(0, 0, 256, 192), (191, 0), "rotate 90: top-left goes top-right (clockwise)");
                Eq(new Orientation(0, true, false).ToDisplay(0, 0, 256, 192), (255, 0), "mirror h: left-right");
                Eq(new Orientation(0, false, true).ToDisplay(0, 0, 256, 192), (0, 191), "mirror v: top-bottom");
                Eq(new Orientation(90, true, false).ToDisplay(0, 0, 256, 192), (0, 0), "rotate first, then mirror");
                var cases = new (WebcamSettings S, (int, int) Hot)[]
                {
                    (new WebcamSettings { MirrorHorizontal = true }, (138, 101)),
                    (new WebcamSettings { MirrorVertical = true }, (501, 378)),
                    (new WebcamSettings { MirrorHorizontal = true, MirrorVertical = true }, (138, 378)),
                    (new WebcamSettings { Rotate = 180 }, (138, 378)),
                    (new WebcamSettings { Rotate = 270 }, (215, 104)),
                    (new WebcamSettings { Rotate = 90, MirrorHorizontal = true }, (215, 375)),
                };
                foreach (var (s, hot) in cases)
                {
                    var r = new FrameRenderer();
                    r.Render(Live(HotFrame(), s), new byte[FrameRenderer.OutBytes]);
                    Eq(r.LastHotSpotPx, ((int, int)?)hot, SettingsWatcher.Describe(s));
                }
            });
            yield return ("renderer: palette, locked range, settings change starts afresh", () =>
            {
                var r = new FrameRenderer();
                var bgra = new byte[FrameRenderer.OutBytes];
                var grey = WebcamSettings.Default with { Palette = "grey", Range = RangeMode.Manual, RangeMin = 20, RangeMax = 30 };
                r.Render(Live(HotFrame(), grey, 1), bgra);
                Eq(PixelAt(bgra, 100, 240), ((byte)127, (byte)127, (byte)127), "25 on a 20..30 grey scale");
                Check(ReferenceEquals(r.LastLut, Palettes.Lut(grey)), "grey table");
                var hotter = HotFrame();
                hotter[10 * 256 + 10] = 90f;
                var locked = WebcamSettings.Default with { Range = RangeMode.Locked };
                r.Render(Live(HotFrame(), locked, 2), bgra);
                Eq(r.LastRange, (25.0, 60.0), "locked: the first frame's range");
                r.Render(Live(hotter, locked, 2), bgra);
                Eq(r.LastRange, (25.0, 60.0), "locked: held");
                r.Render(Live(hotter, locked, 3), bgra);
                Eq(r.LastRange, (25.0, 90.0), "new settings: locked afresh");
                var auto = new FrameRenderer();
                auto.Render(Live(HotFrame()), bgra);
                auto.Render(Live(hotter), bgra);
                Eq(auto.LastRange, (25.0, 90.0), "auto widens at once");
            });
            yield return ("renderer: card and text", () =>
            {
                var bgra = new byte[FrameRenderer.OutBytes];
                new FrameRenderer().Render(Picture.Card(1, WebcamEngine.BusyTitle, "Another program has the TC001 Max open. Retrying every 3 s."), bgra);
                Eq(PixelAt(bgra, 5, 100), ((byte)0x20, (byte)0x24, (byte)0x30), "background");
                Eq(PixelAt(bgra, 0, 0), ((byte)255, (byte)176, (byte)0), "amber stripe");
                Eq(PixelFont.Normalise("abc…–ä°"), "ABC...-?°", "normalise");
                Check(FrameRenderer.Wrap(new string('x', 200), 2).All(l => PixelFont.Measure(l, 2) <= 600), "wrap");
                Check(FrameRenderer.Footer.All(PixelFont.Has), "footer glyphs");
            });

            // reader state machine
            yield return ("session: stall -> calibrating -> resumed", () =>
            {
                var clock = new FakeClock();
                var link = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2, 3)).Then(Act.Stall).Then(Act.Timeout).Stream(Blocks(4, 5));
                var (s, seqs) = RunSession(clock, link);
                Check(seqs.SequenceEqual([2, 3, 5]), $"frames {string.Join(",", seqs)}");
                Eq((s.Calibrations, s.StallClears, s.SoftRestarts, s.Reconnects), (1, 1, 0, 0), "counters");
                Eq(link.ClearHalts, 1, "halt cleared");
                Eq(link.StartStreams, 2, "start_stream twice (start sequence)");
                Check(link.Alts.SequenceEqual([(1, 1), (1, 0)]), "interface 1 alt 1, then 0");
                Check(link.Closed, "closed");
                Eq(s.State, "stopped", "state");
                Eq((s.Model, s.Speed), ("TC001 Max", "high"), "model / speed");
            });
            yield return ("session: no block 3 s after a stall -> start_stream once more", () =>
            {
                var clock = new FakeClock();
                var link = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2)).Then(Act.Stall)
                    .Then(Act.Timeout).Then(Act.Timeout).Then(Act.Timeout).Then(Act.Timeout).Stream(Blocks(6, 7));
                var (s, seqs) = RunSession(clock, link);
                Check(seqs.SequenceEqual([2, 7]), $"frames {string.Join(",", seqs)}");
                Eq((s.Calibrations, s.SoftRestarts, s.Reconnects), (1, 1, 0), "counters");
                Eq(link.StartStreams, 3, "start_stream re-sent once");
            });
            yield return ("session: no block 8 s after a stall -> full reopen", () =>
            {
                var clock = new FakeClock();
                var first = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2)).Then(Act.Stall);
                for (var i = 0; i < 12; i++)
                    first.Then(Act.Timeout);
                var second = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2, 3));
                var (s, seqs) = RunSession(clock, first, second);
                Check(seqs.SequenceEqual([2, 2, 3]), $"frames {string.Join(",", seqs)}");
                Eq((s.Reconnects, s.SoftRestarts), (1, 1), "counters");
                Check(first.Closed && second.Closed, "both closed");
                Check(s.LastError.Length == 0, "error cleared after the reopen");
            });
            yield return ("session: three timeouts outside a calibration -> reopen", () =>
            {
                var clock = new FakeClock();
                var first = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2)).Then(Act.Timeout).Then(Act.Timeout).Then(Act.Timeout);
                var second = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2));
                var (s, seqs) = RunSession(clock, first, second);
                Check(seqs.SequenceEqual([2, 2]), $"frames {string.Join(",", seqs)}");
                Eq((s.Reconnects, s.Calibrations), (1, 0), "counters");
            });
            yield return ("session: too many stalls in a row -> reopen", () =>
            {
                var clock = new FakeClock();
                var first = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2));
                for (var i = 0; i < 25; i++)
                    first.Then(Act.Stall);
                var second = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2));
                var (s, _) = RunSession(clock, first, second);
                Eq(s.Reconnects, 1, "reconnects");
                Eq(first.ClearHalts, 20, "20 clears, then a reopen");
            });
            yield return ("session: auto_shutter off -> switched off after the start, back on at the stop", () =>
            {
                var clock = new FakeClock();
                var link = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2, 3));
                var (s, seqs) = RunSessionWith(clock, () => false, link);
                Check(seqs.SequenceEqual([2, 3]), $"frames {string.Join(",", seqs)}");
                Eq(link.CountOf(Tc001Max.CmdAutoShutterOff), 1, "off sent once");
                Eq(link.CountOf(Tc001Max.CmdAutoShutterOn), 1, "on sent once (at the stop)");
                var cmds = link.Commands;
                Check(cmds.LastIndexOf(HexOf(Tc001Max.CmdStartStream)) < cmds.IndexOf(HexOf(Tc001Max.CmdAutoShutterOff)), "off after the start sequence");
                Check(link.AutoShutter && s.AutoShutter == true, "camera left with its automatic shutter on");
                Check(link.Alts.SequenceEqual([(1, 1), (1, 0)]) && link.Closed, "stopped and closed as usual");
            });
            yield return ("session: auto_shutter on -> nothing sent unless the camera reports off", () =>
            {
                var clock = new FakeClock();
                var normal = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2));
                RunSessionWith(clock, () => true, normal);
                Eq((normal.CountOf(Tc001Max.CmdGetAutoShutter), normal.CountOf(Tc001Max.CmdAutoShutterOn), normal.CountOf(Tc001Max.CmdAutoShutterOff)), (1, 0, 0), "only read");
                var leftOff = new FakeLink(clock) { AutoShutter = false }.Then(Act.Timeout).Stream(Blocks(1, 2));
                var (s, _) = RunSessionWith(clock, () => true, leftOff);
                Eq(leftOff.CountOf(Tc001Max.CmdAutoShutterOn), 1, "left off by an earlier session: switched on");
                Check(leftOff.AutoShutter && s.AutoShutter == true, "on");
            });
            yield return ("session: no auto_shutter wish -> no auto-shutter command at all", () =>
            {
                var clock = new FakeClock();
                var link = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2));
                RunSession(clock, link);
                Check(link.Commands.All(c => !c.StartsWith("1002")), string.Join(" ", link.Commands));
            });
            yield return ("session: auto_shutter changed while streaming", () =>
            {
                var clock = new FakeClock();
                var want = true;
                var link = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2, 3, 4, 5, 6));
                var frames = 0;
                var next = 0;
                CameraSession? session = null;
                session = new CameraSession(() => { link.Session = session; return next++ == 0 ? link : new FakeLink(clock); }, clock) { WantAutoShutter = () => want };
                session.OnFrame = f => { if (++frames == 2) want = false; };
                var t = new Thread(session.Run) { IsBackground = true };
                t.Start();
                Check(t.Join(10_000), "finished");
                Eq(link.CountOf(Tc001Max.CmdAutoShutterOff), 1, "off sent after the change");
                Check(link.AutoShutter, "restored at the stop");
            });
            yield return ("session: idle_auto_shutter is applied on a clean close only", () =>
            {
                var clock = new FakeClock();
                CameraSession Session(FakeLink link, bool? idle)
                {
                    var used = false;
                    CameraSession? s = null;
                    s = new CameraSession(() => { var l = used ? new FakeLink(clock) : link; used = true; l.Session = s; return l; }, clock)
                    {
                        WantAutoShutter = () => true,
                        IdleAutoShutter = () => idle,
                    };
                    var t = new Thread(s.Run) { IsBackground = true };
                    t.Start();
                    Check(t.Join(10_000), "finished");
                    return s;
                }
                var off = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2));
                Session(off, false);
                Check(!off.AutoShutter && off.CountOf(Tc001Max.CmdAutoShutterOff) == 1, "left off (no click while idle)");
                var cmds = off.Commands;
                Check(cmds.LastIndexOf(HexOf(Tc001Max.CmdAutoShutterOff)) > cmds.LastIndexOf(HexOf(Tc001Max.CmdStartStream)), "sent at the close");
                Check(off.Alts.SequenceEqual([(1, 1), (1, 0)]) && off.Closed, "then stopped and closed");

                var on = new FakeLink(clock) { AutoShutter = false }.Then(Act.Timeout).Stream(Blocks(1, 2));
                var s2 = Session(on, true);
                Check(on.AutoShutter && on.CountOf(Tc001Max.CmdAutoShutterOff) == 0, "idle on: left on");

                var keep = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2));
                Session(keep, null);
                Check(keep.AutoShutter && keep.CountOf(Tc001Max.CmdAutoShutterOff) == 0 && keep.CountOf(Tc001Max.CmdAutoShutterOn) == 0, "unchanged: nothing sent");

                // a failed link (the stream stopped) sends nothing; the next link closes cleanly
                var failed = new FakeLink(clock).Then(Act.Timeout).Stream(Blocks(1, 2)).Then(Act.Timeout).Then(Act.Timeout).Then(Act.Timeout);
                var s4 = Session(failed, false);
                Eq(s4.Reconnects, 1, "reconnected after the failure");
                Eq(failed.CountOf(Tc001Max.CmdAutoShutterOff), 0, "nothing sent on a failed link");
            });
            yield return ("session: busy retries every 0.5 s until FastRetryUntil, then every 3 s", () =>
            {
                var clock = new FakeClock();
                var opens = new List<double>();
                CameraSession? s = null;
                s = new CameraSession(() =>
                {
                    opens.Add(clock.Monotonic);
                    if (opens.Count >= 14)
                        s!.RequestStop();
                    return new FakeLink(clock) { Busy = true };
                }, clock) { FastRetryUntil = clock.Monotonic + 5.0 };
                var t = new Thread(s.Run) { IsBackground = true };
                t.Start();
                Check(t.Join(5000), "finished");
                var gaps = opens.Zip(opens.Skip(1), (a, b) => Math.Round(b - a, 3)).ToList();
                Check(gaps.Take(10).All(g => g == 0.5), "fast: " + string.Join(" ", gaps));
                Check(gaps.Skip(10).All(g => g == 3.0), "then normal: " + string.Join(" ", gaps));
            });
            yield return ("session: auto-shutter commands failing never stop the stream", () =>
            {
                var clock = new FakeClock();
                var link = new FakeLink(clock) { AutoShutterFails = true }.Then(Act.Timeout).Stream(Blocks(1, 2, 3));
                var (s, seqs) = RunSessionWith(clock, () => false, link);
                Check(seqs.SequenceEqual([2, 3]), $"frames {string.Join(",", seqs)}");
                Eq(link.Commands.Count(c => c == HexOf(Tc001Max.CmdAutoShutterOff)), 1, "tried once, not in a loop");
                Eq(s.Reconnects, 0, "no reconnect");
            });
            yield return ("session: camera busy is reported as busy", () =>
            {
                var clock = new FakeClock();
                var s = new CameraSession(() => new FakeLink(clock) { Busy = true }, clock);
                var t = new Thread(s.Run) { IsBackground = true };
                t.Start();
                WaitFor(() => s.State == "reconnecting" && s.LastErrorBusy, 5, "busy");
                s.RequestStop();
                Check(t.Join(2000), "stopped");
                Check(s.LastErrorBusy && s.Advice.Length > 0, "busy with advice");
                Check(!s.DeviceOpen, "nothing open");
            });

            // claims shared with labbench (labbench.thermal.ownership)
            yield return ("claims: labbench's claim format is read, the webcam's written in it", () =>
            {
                // exactly what labbench's CameraArbiter.heartbeat writes (json.dumps, one line)
                var py = "{\"version\": 1, \"role\": \"run_host\", \"priority\": 30, \"pid\": 8556, \"host\": \"GOOB\", " +
                         "\"created\": 1795214644.1, \"t_wall\": 1795214923.03, \"expires_s\": 5.0, \"state\": \"holding\", " +
                         "\"source\": \"run 20261121-1300 (run host)\"}";
                var c = Claims.Parse(py, "x")!;
                Eq((c.Role, c.Priority, c.Pid, c.Host, c.State), ("run_host", 30, 8556, "GOOB", "holding"), "fields");
                Eq((c.Created, c.TWall, c.ExpiresS), ((double?)1795214644.1, 1795214923.03, 5.0), "times");
                Eq(c.Describe(), "run 20261121-1300 (run host) (pid 8556)", "describe like labbench");
                Eq(Claims.Parse("{\"role\": \"gui\", \"pid\": 7, \"t_wall\": 1}", "x")!.Priority, 20, "priority from the role");
                Check(Claims.Parse("not json", "x") == null && Claims.Parse("[1]", "x") == null && Claims.Parse("{\"pid\": 1}", "x") == null, "not claims");

                var mine = Claims.Parse(Claims.Format(4242, "GOOB", null, 1800000000.5, holding: true, "TC001 Max Thermal Webcam"), "y")!;
                Eq((mine.Role, mine.Priority, mine.Pid, mine.State, mine.Created), ("webcam", 10, 4242, "holding", (double?)null), "webcam claim");
                using var doc = JsonDocument.Parse(Claims.Format(4242, "GOOB", 1.5, 2.5, holding: false, "w"));
                var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
                Check(keys.SequenceEqual(["version", "role", "priority", "pid", "host", "created", "t_wall", "expires_s", "state", "source"]), string.Join(",", keys));
                Eq(Claims.FileName(4242), "claim-webcam-4242.json", "file name (claim-<role>-<pid>.json)");
            });
            yield return ("claims: only fresh, higher, other processes' claims count", () =>
            {
                Claim C(string role, int prio, int pid, double tWall, string host = "GOOB") =>
                    new(role, prio, pid, host, null, tWall, 5.0, "holding", role, "p");
                bool Alive(int pid, double? _) => pid != 666;
                const double now = 1000.0;
                Check(Claims.Higher([C("webcam", 10, 7, now)], 1, "GOOB", now, Alive) == null, "another webcam is not above us");
                Eq(Claims.Higher([C("gui", 20, 7, now), C("run_host", 30, 8, now)], 1, "GOOB", now, Alive)?.Role, "run_host", "the highest");
                Check(Claims.Higher([C("gui", 20, 7, now - 5.5)], 1, "GOOB", now, Alive) == null, "expired");
                Check(Claims.Higher([C("gui", 20, 666, now)], 1, "GOOB", now, Alive) == null, "its process is gone");
                Check(Claims.Higher([C("gui", 20, 666, now, "OTHER")], 1, "GOOB", now, Alive) != null, "another computer: heartbeat only");
                Check(Claims.Higher([C("gui", 20, 1, now)], 1, "goob", now, Alive) == null, "our own pid on this computer");
                Check(ProcessInfo.Alive(Environment.ProcessId, ProcessInfo.Created(Environment.ProcessId)), "this process is alive");
                Check(!ProcessInfo.Alive(Environment.ProcessId, ProcessInfo.Created(Environment.ProcessId) - 100), "another creation time: a reused pid");
            });
            yield return ("claims: folder read skips junk, keeps the newest per role + pid; write and delete", () =>
            {
                var dir = TempDir();
                try
                {
                    File.WriteAllText(Path.Combine(dir, "claim-gui-7.json"), "{\"role\": \"gui\", \"pid\": 7, \"t_wall\": 5}");
                    File.WriteAllText(Path.Combine(dir, "claim-gui-7-old.json"), "{\"role\": \"gui\", \"pid\": 7, \"t_wall\": 3}");
                    File.WriteAllText(Path.Combine(dir, "claim-junk.json"), "{{{");
                    File.WriteAllText(Path.Combine(dir, "claim-big.json"), "{\"role\": \"gui\", \"pid\": 9, \"t_wall\": 5, \"x\": \"" + new string('a', 20000) + "\"}");
                    File.WriteAllText(Path.Combine(dir, "other.json"), "{\"role\": \"gui\", \"pid\": 8, \"t_wall\": 5}");
                    var read = Claims.Read(dir);
                    Check(read.Count == 1 && read[0].Pid == 7 && read[0].TWall == 5, string.Join(" ", read));
                    Claims.Write(dir, Claims.Format(42, "GOOB", null, 1, true, "w"), 42);
                    Claims.Write(dir, Claims.Format(42, "GOOB", null, 2, false, "w"), 42);
                    var w = Claims.Read(dir).Single(c => c.Pid == 42);
                    Eq((w.TWall, w.State), (2.0, "wanted"), "replaced");
                    Check(!File.Exists(Path.Combine(dir, "claim-webcam-42.json.tmp")), "no temporary file left");
                    Claims.Delete(dir, 42);
                    Check(!File.Exists(Path.Combine(dir, "claim-webcam-42.json")), "deleted");
                    Claims.Delete(dir, 42); // twice is harmless
                    Check(Claims.Read(Path.Combine(dir, "missing")).Count == 0, "a missing folder reads as no claims");
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
            });

            // engine
            yield return ("engine: owns the camera while streaming, follows settings, releases on stop", EngineTest);
            yield return ("engine: lends the camera to labbench and takes it back", EngineHandoffTest);
            yield return ("engine: camera in use by another program / not found", () =>
            {
                var clock = new FakeClock();
                using (var busy = new WebcamEngine(new EngineOptions { Clock = clock, LinkFactory = () => new FakeLink(clock) { Busy = true } }))
                {
                    Eq(busy.Current().CardTitle, FrameRenderer.Footer, "not started: a starting card");
                    busy.Start();
                    WaitFor(() => busy.Current().CardTitle == WebcamEngine.BusyTitle, 5, "the busy card");
                    var card = busy.Current();
                    Check(card.Kind == PictureKind.Card && card.CardText.Contains("Retrying every 3 s"), card.CardText);
                    Check(card.CardTitle.All(PixelFont.Has) && card.CardText.All(PixelFont.Has), "drawable text");
                }
                var missing = new CameraUnavailableException("TC001 Max not found (USB 3474:4962)", "Plug it into a USB 3 port.");
                using var none = new WebcamEngine(new EngineOptions { Clock = clock, LinkFactory = () => new FakeLink(clock) { OpenError = missing } });
                none.Start();
                WaitFor(() => none.Current().CardTitle == WebcamEngine.NoCameraTitle, 5, "the no-camera card");
                var text = none.Current().CardText;
                Check(text.Contains("not found") && text.Contains("Plug it") && text.Contains("Retrying"), text);
            });
            yield return ("engine: the process-wide instance follows the stream count", () =>
            {
                var clock = new FakeClock();
                var links = new List<FakeLink>();
                EngineOptions Options() => new()
                {
                    Clock = clock,
                    LinkFactory = () =>
                    {
                        var l = new FakeLink(clock) { Endless = true };
                        lock (links) links.Add(l);
                        return l;
                    },
                };
                var a = WebcamEngine.Acquire(Options);
                var b = WebcamEngine.Acquire(Options);
                Check(ReferenceEquals(a, b), "one engine per process");
                WaitFor(() => a.Session is { DeviceOpen: true }, 5, "open");
                WebcamEngine.Release();
                Check(a.Active && a.ReaderRunning, "one stream left: still reading");
                WebcamEngine.Release();
                Check(!a.Active, "no stream: stopped");
                WaitFor(() => !a.ReaderRunning, 2, "the reader to close");
                lock (links)
                    Check(links.Count == 1 && links[0].Closed, "opened once, closed");
                WebcamEngine.Release(); // extra releases are harmless
            });
        }

        private static void BlockStats(DecodedBlock d, JsonElement e)
        {
            var t = d.TemperatureC;
            Eq(d.Sequence, e.GetProperty("sequence").GetInt32(), "sequence");
            var finite = t.Where(float.IsFinite).ToArray();
            Eq(t.Length - finite.Length, e.GetProperty("nan").GetInt32(), "NaN count");
            Eq((double)finite.Max(), e.GetProperty("max_c").GetDouble(), "max");
            Eq((double)finite.Min(), e.GetProperty("min_c").GetDouble(), "min");
            var arg = Array.IndexOf(t, finite.Max());
            Eq((arg % 256, arg / 256), (e.GetProperty("argmax")[0].GetInt32(), e.GetProperty("argmax")[1].GetInt32()), "argmax");
            var sorted = finite.OrderBy(v => v).ToArray();
            var median = (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2f;
            Near(median, e.GetProperty("median_c").GetDouble(), 1e-4, "median");
            foreach (var smp in e.GetProperty("samples").EnumerateArray())
            {
                int x = smp[0].GetInt32(), y = smp[1].GetInt32();
                Eq((double)t[y * 256 + x], smp[2].GetDouble(), $"temperature ({x}, {y})");
            }
            foreach (var smp in e.GetProperty("grey_samples").EnumerateArray())
            {
                int x = smp[0].GetInt32(), y = smp[1].GetInt32();
                Eq((int)d.Image[y * 256 + x], smp[2].GetInt32(), $"grey ({x}, {y})");
            }
        }

        /// <summary>The reference smoother frame: float64 maths, then float32.</summary>
        private static float[] SmootherFrame(int i, (int X, int Y, float V)? hot, bool noise, int nanRow0)
        {
            var f = new float[48 * 64];
            for (var y = 0; y < 48; y++)
                for (var x = 0; x < 64; x++)
                    f[y * 64 + x] = noise ? (float)(25.0 + (((x * 31 + y * 17 + i * 13) % 7) - 3) * 0.1) : 25f;
            if (hot is { } h)
                f[h.Y * 64 + h.X] = h.V;
            for (var x = 0; x < nanRow0; x++)
                f[x] = float.NaN;
            return f;
        }

        private static (CameraSession, List<int>) RunSession(FakeClock clock, params FakeLink[] links) =>
            RunSessionWith(clock, null, links);

        private static (CameraSession, List<int>) RunSessionWith(FakeClock clock, Func<bool>? wantAutoShutter, params FakeLink[] links)
        {
            var next = 0;
            CameraSession? session = null;
            session = new CameraSession(() =>
            {
                var l = next < links.Length ? links[next++] : new FakeLink(clock);
                l.Session = session;
                return l;
            }, clock) { WantAutoShutter = wantAutoShutter };
            var seqs = new List<int>();
            session.OnFrame = f => seqs.Add(f.Sequence);
            var t = new Thread(session.Run) { IsBackground = true };
            t.Start();
            if (!t.Join(10_000))
            {
                session.RequestStop();
                throw new Exception("the session did not finish");
            }
            return (session, seqs);
        }

        /// <summary>A labbench claim appears (the webcam closes the camera, shows the lent card, drops its
        /// claim) and goes away again (the webcam reopens the camera and claims it again).</summary>
        private static void EngineHandoffTest()
        {
            var dir = TempDir();
            var clock = new FakeClock();
            var links = new List<FakeLink>();
            var overlap = false;
            var engine = new WebcamEngine(new EngineOptions
            {
                Clock = clock,
                ClaimDir = dir,
                Pid = 4242,
                Host = "GOOB",
                Created = 1.0,
                Alive = (_, _) => true,
                LinkFactory = () =>
                {
                    var l = new FakeLink(clock) { Endless = true };
                    lock (links)
                    {
                        if (links.Any(old => !old.Closed))
                            overlap = true;
                        links.Add(l);
                    }
                    return l;
                },
            });
            var mine = Path.Combine(dir, "claim-webcam-4242.json");
            var theirs = Path.Combine(dir, "claim-gui-555.json");
            Claim? Mine() => File.Exists(mine) ? Claims.Read(dir).FirstOrDefault(c => c.Pid == 4242) : null;
            try
            {
                // 1. streaming: the webcam holds the camera and says so
                engine.Start();
                WaitFor(() => engine.Session is { DeviceOpen: true, FramesDelivered: > 0 }, 5, "frames");
                WaitFor(() => Mine()?.State == "holding", 3, "a holding claim");
                var claim = Mine()!;
                Eq((claim.Role, claim.Priority, claim.Host, claim.Source), ("webcam", 10, "GOOB", "TC001 Max Thermal Webcam"), "claim");

                // 2. labbench's GUI claims it (expires_s large: the fake clock runs fast)
                File.WriteAllText(theirs, "{\"version\": 1, \"role\": \"gui\", \"priority\": 20, \"pid\": 555, \"host\": \"GOOB\", " +
                                          $"\"created\": null, \"t_wall\": {clock.Wall.ToString(CultureInfo.InvariantCulture)}, \"expires_s\": 1e12, " +
                                          "\"state\": \"wanted\", \"source\": \"labbench GUI\"}");
                var t0 = DateTime.UtcNow;
                WaitFor(() => !engine.ReaderRunning, 2, "the camera closed for labbench");
                Check((DateTime.UtcNow - t0).TotalSeconds < 1.5, "yielded within about a second");
                WaitFor(() => !File.Exists(mine), 2, "the webcam's claim removed");
                lock (links)
                    Check(links.Count == 1 && links[0].Closed && !links[0].AutoShutter, "closed, auto shutter left off (idle default)");
                var card = engine.Current();
                Eq(card.CardTitle, WebcamEngine.LentTitle, "the lent card");
                Check(card.CardText.Contains("labbench GUI (pid 555)") && card.CardText.Contains("comes back"), card.CardText);
                Eq(engine.LentTo?.Pid, 555, "lent to");
                Thread.Sleep(400);
                lock (links)
                    Eq(links.Count, 1, "not reopened while labbench claims it");

                // 3. labbench lets go: the webcam takes the camera back by itself
                File.Delete(theirs);
                WaitFor(() => engine.Session is { DeviceOpen: true, FramesDelivered: > 0 }, 5, "reopened");
                WaitFor(() => engine.Current().Kind == PictureKind.Live, 2, "live again");
                WaitFor(() => Mine()?.State == "holding", 3, "claimed again");
                Check(engine.LentTo == null, "not lent any more");
                Check(engine.Session!.FastRetryUntil > clock.Monotonic - 60, "fast busy retries armed after the handoff");

                // 4. the stream stops: camera closed, claim gone
                engine.Stop();
                WaitFor(() => !engine.ReaderRunning, 2, "closed");
                WaitFor(() => !File.Exists(mine), 2, "claim removed at the stop");
                lock (links)
                    Check(links.Count == 2 && links.All(l => l.Closed), "two opens, both closed");
                Check(!overlap, "never two readers on the device");
            }
            finally
            {
                engine.Dispose();
                Directory.Delete(dir, true);
            }
        }

        private static void EngineTest()
        {
            var dir = TempDir();
            var clock = new FakeClock();
            var links = new List<FakeLink>();
            var overlap = false;
            var settingsPath = Path.Combine(dir, "settings.json");
            var engine = new WebcamEngine(new EngineOptions
            {
                Clock = clock,
                SettingsPath = settingsPath,
                SettingsPollS = 0,
                LinkFactory = () =>
                {
                    var l = new FakeLink(clock) { Endless = true };
                    lock (links)
                    {
                        if (links.Any(old => !old.Closed))
                            overlap = true; // the previous reader must have let go first
                        links.Add(l);
                    }
                    return l;
                },
            });
            try
            {
                SettingsFile.Save(settingsPath, WebcamSettings.Default with { Rotate = 90 });
                Eq(engine.Current().Kind, PictureKind.Card, "idle: a card");
                Check(!engine.ReaderRunning, "idle: the camera is not opened");

                // 1. a stream starts: read the settings, open the camera, show its frames
                engine.Start();
                engine.Start(); // idempotent
                WaitFor(() => engine.Session is { DeviceOpen: true, FramesDelivered: > 0 }, 5, "frames");
                WaitFor(() => engine.Current().Kind == PictureKind.Live, 2, "a live picture");
                var pic = engine.Current();
                Eq((pic.Source, pic.Settings.Rotate, pic.Width, pic.Height), (WebcamEngine.Source, 90, 256, 192), "live picture with the settings");
                var v1 = pic.Version;
                WaitFor(() => engine.Current().Version != v1, 5, "a newer picture");
                lock (links)
                    Eq(links.Count, 1, "one open");

                // 2. settings.json changes: picked up without a restart
                var sv = pic.SettingsVersion;
                SettingsFile.Save(settingsPath, WebcamSettings.Default with { Rotate = 90, Palette = "grey", Units = TempUnits.F });
                File.SetLastWriteTimeUtc(settingsPath, DateTime.UtcNow.AddSeconds(5));
                pic = engine.Current();
                Eq((pic.Settings.Palette, pic.Settings.Units), ("grey", TempUnits.F), "new settings");
                Check(pic.SettingsVersion > sv, "settings version grew");

                // 3. the stream stops: the camera is released within about a second
                var t0 = DateTime.UtcNow;
                engine.Stop();
                WaitFor(() => !engine.ReaderRunning, 1.5, "the reader to close");
                Check((DateTime.UtcNow - t0).TotalSeconds < 1.0, "closed within a second");
                lock (links)
                    Check(links[0].Closed && links[0].Aborts > 0 && links[0].Alts.LastOrDefault() == (1, 0), "aborted, alt 0, closed");
                Eq(engine.Current().Kind, PictureKind.Card, "stopped: a card");

                // 4. stop and start at once: the new reader waits for the old one to let go
                engine.Start();
                WaitFor(() => engine.Session is { DeviceOpen: true }, 5, "reopened");
                engine.Stop();
                engine.Start();
                WaitFor(() => engine.Session is { DeviceOpen: true, FramesDelivered: > 0 }, 5, "reopened again");
                lock (links)
                    Eq(links.Count, 3, "three opens");
                Check(!overlap, "never two readers on the device");
            }
            finally
            {
                engine.Dispose();
                lock (links)
                    Check(links.All(l => l.Closed), "all links closed on dispose");
                Directory.Delete(dir, true);
            }
        }
    }

    internal sealed class FakeClock : IClock
    {
        private readonly object _l = new();
        private double _mono = 100.0;
        private double _wallBase = 1_800_000_000.0;

        public double Monotonic { get { lock (_l) return _mono; } }
        public double Wall { get { lock (_l) return _wallBase + _mono; } }

        public void Advance(double s)
        {
            lock (_l) _mono += s;
        }

        public bool Sleep(double seconds, WaitHandle stop)
        {
            if (stop.WaitOne(0))
                return false;
            Advance(seconds);
            return true;
        }
    }

    internal enum Act { Bytes, Timeout, Stall }

    /// <summary>A scripted TC001 Max: control requests succeed, bulk reads follow the script; when the
    /// script ends the session is asked to stop (or, Endless, blocks keep coming).</summary>
    internal sealed class FakeLink(FakeClock clock) : IUsbLink
    {
        private readonly LinkedList<(Act Act, byte[]? Data)> _script = new();
        private byte[] _endless = [];
        private int _endlessPos;
        private int _endlessSeq = 1;
        private volatile bool _aborted;

        public CameraSession? Session { get; set; }
        public bool Busy { get; init; }
        public CameraUnavailableException? OpenError { get; init; }
        public bool Endless { get; init; }
        public int StartStreams { get; private set; }
        public int ClearHalts { get; private set; }
        public int Aborts { get; private set; }
        public volatile bool Closed;
        public List<(int Iface, int Alt)> Alts { get; } = [];
        public string Speed => "high";

        public FakeLink Then(Act act)
        {
            _script.AddLast((act, null));
            return this;
        }

        public FakeLink Stream(byte[] data)
        {
            for (var i = 0; i < data.Length; i += Tc001Max.BulkReadBytes)
                _script.AddLast((Act.Bytes, data.AsSpan(i, Math.Min(Tc001Max.BulkReadBytes, data.Length - i)).ToArray()));
            return this;
        }

        public void Open()
        {
            if (Busy)
                throw new CameraUnavailableException("the TC001 Max is in use by another program", "WinUSB lets one program open it at a time.", busy: true);
            if (OpenError != null)
                throw OpenError;
        }

        /// <summary>The camera's auto-shutter switch (on by default; kept across opens like the real one).</summary>
        public bool AutoShutter { get; set; } = true;
        /// <summary>Every 18-byte command sent, as hex, in order.</summary>
        public List<string> Commands { get; } = [];
        /// <summary>Auto-shutter commands fail with a USB error (an older firmware without them).</summary>
        public bool AutoShutterFails { get; init; }
        private byte[] _lastCommand = [];

        public int ControlOut(byte requestType, byte request, ushort value, ushort index, byte[] data, int timeoutMs)
        {
            if (Closed) throw new UsbErrorException("closed");
            if (request == Tc001Max.ReqCommand)
            {
                lock (Commands)
                    Commands.Add(Convert.ToHexString(data).ToLowerInvariant());
                _lastCommand = data;
                var ffc = data.AsSpan().SequenceEqual(Tc001Max.CmdAutoShutterOn) || data.AsSpan().SequenceEqual(Tc001Max.CmdAutoShutterOff)
                          || data.AsSpan().SequenceEqual(Tc001Max.CmdGetAutoShutter);
                if (ffc && AutoShutterFails)
                    throw new UsbErrorException("pipe error");
                if (data.AsSpan().SequenceEqual(Tc001Max.CmdAutoShutterOn))
                    AutoShutter = true;
                else if (data.AsSpan().SequenceEqual(Tc001Max.CmdAutoShutterOff))
                    AutoShutter = false;
            }
            if (data.AsSpan().SequenceEqual(Tc001Max.CmdStartStream))
                StartStreams++;
            return data.Length;
        }

        public byte[] ControlIn(byte requestType, byte request, ushort value, ushort index, int length, int timeoutMs)
        {
            if (Closed) throw new UsbErrorException("closed");
            var r = new byte[length];
            if (request == Tc001Max.ReqResponse && length == 30)
                Encoding.ASCII.GetBytes("TC001 Max").CopyTo(r, 0);
            if (request == Tc001Max.ReqResponse && length == 1 && _lastCommand.AsSpan().SequenceEqual(Tc001Max.CmdGetAutoShutter))
                r[0] = (byte)(AutoShutter ? 1 : 0);
            return r; // status 0: not busy
        }

        public int CountOf(byte[] cmd)
        {
            var hex = Convert.ToHexString(cmd).ToLowerInvariant();
            lock (Commands)
                return Commands.Count(c => c == hex);
        }

        public void SetAlt(ushort iface, byte alt)
        {
            lock (Alts)
                Alts.Add((iface, alt));
        }

        public int BulkRead(byte endpoint, byte[] buffer, int timeoutMs)
        {
            if (Closed || _aborted)
                throw new UsbErrorException("aborted");
            if (Endless)
            {
                Thread.Sleep(1);
                clock.Advance(0.005);
                if (_endlessPos >= _endless.Length)
                {
                    _endless = [.. Tc001Max.SyntheticBlock(Enumerable.Repeat(22f + _endlessSeq % 3, Tc001Max.Pixels).ToArray(), _endlessSeq++), .. new byte[600]];
                    _endlessPos = 0;
                }
                var n = Math.Min(buffer.Length, _endless.Length - _endlessPos);
                Array.Copy(_endless, _endlessPos, buffer, 0, n);
                _endlessPos += n;
                return n;
            }
            if (_script.Count == 0)
            {
                Session?.RequestStop();
                throw new UsbTimeoutException("script finished");
            }
            var (act, data) = _script.First!.Value;
            _script.RemoveFirst();
            switch (act)
            {
                case Act.Timeout:
                    clock.Advance(timeoutMs / 1000.0);
                    throw new UsbTimeoutException("timeout");
                case Act.Stall:
                    throw new UsbStallException("stall");
                default:
                    data!.CopyTo(buffer, 0);
                    clock.Advance(0.005);
                    return data.Length;
            }
        }

        public void ClearHalt(byte endpoint) => ClearHalts++;

        public void Abort()
        {
            Aborts++;
            _aborted = true;
        }

        public void Close() => Closed = true;
    }
}
