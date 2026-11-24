// TC001 Max Thermal Webcam: the picture settings (settings.json in C:\ProgramData\TC001MaxWebcam).
//
// Pure code apart from SettingsWatcher's file reads. The media source reads the file when a stream
// starts and again whenever its modification time or size changes (polled about once a second).
// Reading is lenient: a bad value is reported (webcam.log, "TC001MaxWebcam settings") and that key
// falls back to its default, so one typo never stops the picture. Writing (TC001MaxWebcam settings
// set key=value) is strict: nothing is written unless every value is valid.
//
// Keys (JSON, snake_case):
//   rotate             0 | 90 | 180 | 270   clockwise, applied before mirroring
//   mirror_horizontal  true | false         left-right
//   mirror_vertical    true | false         top-bottom
//   units              "C" | "F"            on-image readouts, colour scale and range_min/range_max
//   palette            inferno | iron | rainbow | grey | black_hot | custom   (labbench's palettes;
//                                           white_hot = grey, turbo = rainbow also accepted)
//   custom_palette     2-5 colours "#rrggbb", cold to hot (used when palette is custom)
//   range              auto | locked | manual
//                        auto: follows the scene (1st percentile .. hottest pixel, whole degrees)
//                        locked: the auto range of the first frame, held until the stream stops
//                                or the settings change
//                        manual: range_min .. range_max
//   range_speed        slow | normal | fast | instant   how fast the auto range narrows after the
//                                           scene cools (it always widens at once): about 25 s,
//                                           12 s, 3 s or the next frame to settle (time constant
//                                           8, 4, 1, 0 s)
//   range_min          number, in units
//   range_max          number, in units (must be above range_min)
//   auto_shutter       "on" | "off"         the camera's automatic shutter / NUC (about once a
//                                           minute). off: no click and no pause, but the picture's
//                                           fixed-pattern noise grows and readings drift.
//                                           See docs/PROTOCOL.md "Automatic shutter".
//   idle_auto_shutter  "off" | "on" | "unchanged"   what the automatic shutter is left at when the
//                                           webcam closes the camera (the last app stopped, or it
//                                           lends the camera to labbench). off (default, the same as
//                                           labbench's idle_auto_shutter): no click while nothing
//                                           streams. The camera keeps the switch until changed.
//   overlay            true | false         false: the bare false-colour picture, none of the
//                                           marks below (status badges and cards still show)
//   crosshair          true | false         the small x at the centre of the picture
//   hotspot            true | false         the green + on the hottest pixel, with its temperature
//   readouts           true | false         the top bar: MAX / CENTRE temperatures
//   scale              true | false         the bottom bar: colour scale, range mode, time, source
//   denoise            true | false         noise reduction on the temperatures (Denoiser.cs): shutter
//                                           table averaging, flicker removal and a motion-adaptive
//                                           temporal filter; readouts and the range use the result.
//                                           Default false.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TC001MaxWebcam.Core
{
    public enum TempUnits { C, F }

    public enum RangeMode { Auto, Locked, Manual }

    /// <summary>How fast the auto range narrows (range_speed).</summary>
    public enum RangeSpeed { Slow, Normal, Fast, Instant }

    /// <summary>A value that cannot be used, with a message for the owner.</summary>
    public sealed class SettingsException(string message) : Exception(message);

    public sealed record WebcamSettings
    {
        public static readonly WebcamSettings Default = new();

        public int Rotate { get; init; }
        public bool MirrorHorizontal { get; init; }
        public bool MirrorVertical { get; init; }
        public TempUnits Units { get; init; } = TempUnits.C;
        public string Palette { get; init; } = "inferno";
        public string[] CustomPalette { get; init; } = ["#000040", "#00a0ff", "#ffff00", "#ff0000"]; // labbench's default
        public RangeMode Range { get; init; } = RangeMode.Auto;
        public RangeSpeed RangeSpeed { get; init; } = RangeSpeed.Normal;

        /// <summary>The auto range's narrowing time constant in seconds for <see cref="RangeSpeed"/>.</summary>
        public double RangeTauS => RangeSpeed switch
        {
            RangeSpeed.Slow => 8.0,
            RangeSpeed.Fast => 1.0,
            RangeSpeed.Instant => 0.0,
            _ => 4.0,
        };
        public double RangeMin { get; init; } = 20.0;
        public double RangeMax { get; init; } = 40.0;
        public bool AutoShutter { get; init; } = true;
        /// <summary>idle_auto_shutter: false = off (default), true = on, null = unchanged.</summary>
        public bool? IdleAutoShutter { get; init; } = false;
        public bool Overlay { get; init; } = true;
        public bool Crosshair { get; init; } = true;
        public bool HotSpot { get; init; } = true;
        public bool Readouts { get; init; } = true;
        public bool Scale { get; init; } = true;
        public bool Denoise { get; init; }

        public Orientation Orientation => new(Rotate, MirrorHorizontal, MirrorVertical);

        /// <summary>"°C" or "°F".</summary>
        public string UnitSymbol => Units == TempUnits.F ? "°F" : "°C";

        /// <summary>The defaults, with the default manual range expressed in <paramref name="units"/>.</summary>
        public static WebcamSettings DefaultsFor(TempUnits units) => units == TempUnits.C
            ? Default
            : Default with { Units = units, RangeMin = Temperature.ToUnits(Default.RangeMin, units), RangeMax = Temperature.ToUnits(Default.RangeMax, units) };
    }

    public static class Temperature
    {
        /// <summary>degC to <paramref name="units"/> (F = C x 9 / 5 + 32).</summary>
        public static double ToUnits(double celsius, TempUnits units) => units == TempUnits.F ? celsius * 9.0 / 5.0 + 32.0 : celsius;

        public static float ToUnits(float celsius, TempUnits units) => units == TempUnits.F ? celsius * 9f / 5f + 32f : celsius;

        public static double ToCelsius(double value, TempUnits units) => units == TempUnits.F ? (value - 32.0) * 5.0 / 9.0 : value;

        /// <summary>A whole frame in <paramref name="units"/> (the same array for degC; NaN stays NaN).</summary>
        public static float[] FrameToUnits(float[] celsius, TempUnits units, ref float[]? scratch)
        {
            if (units == TempUnits.C)
                return celsius;
            if (scratch == null || scratch.Length != celsius.Length)
                scratch = new float[celsius.Length];
            for (var i = 0; i < celsius.Length; i++)
                scratch[i] = celsius[i] * 9f / 5f + 32f;
            return scratch;
        }
    }

    public static class SettingsFile
    {
        /// <summary>Manual range limits in degC (the protocol's plausibility floor and ceiling).</summary>
        public const double MinC = Tc001Max.FloorC;
        public const double MaxC = Tc001Max.CeilingC;

        public static readonly string[] Keys =
            ["rotate", "mirror_horizontal", "mirror_vertical", "units", "palette", "custom_palette", "range", "range_speed", "range_min", "range_max", "auto_shutter", "idle_auto_shutter",
             "overlay", "crosshair", "hotspot", "readouts", "scale", "denoise"];

        public static readonly string[] Help =
        [
            "rotate=0|90|180|270             clockwise, before mirroring",
            "mirror_horizontal=true|false    left-right",
            "mirror_vertical=true|false      top-bottom",
            "units=C|F                       readouts, colour scale, range_min/range_max",
            "palette=inferno|iron|rainbow|grey|black_hot|custom",
            "custom_palette=#000000,#ff0000,#ffffff   2-5 colours, cold to hot",
            "range=auto|locked|manual",
            "range_min=<number>              manual range, in units",
            "range_max=<number>              manual range, in units (above range_min)",
            "range_speed=slow|normal|fast|instant   how fast the auto range narrows (~25 s, 12 s, 3 s, at once)",
            "auto_shutter=on|off             camera's automatic shutter (off: no click, more drift)",
            "idle_auto_shutter=off|on|unchanged   left so when the webcam closes the camera (default off)",
            "overlay=true|false              false: bare picture, no marks or bars",
            "crosshair=true|false            centre mark",
            "hotspot=true|false              hottest-pixel mark and its temperature",
            "readouts=true|false             top bar: MAX / CENTRE",
            "scale=true|false                bottom bar: colour scale, range mode, time",
            "denoise=true|false              noise reduction (temporal filter, flicker, shutter tables)",
        ];

        private static readonly JsonDocumentOptions JsonOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        // -- one value -----------------------------------------------------------------------------
        /// <summary>Returns <paramref name="s"/> with <paramref name="key"/> set from its text form
        /// (SettingsException if the key is unknown or the value invalid). No cross-key checks.</summary>
        public static WebcamSettings With(WebcamSettings s, string key, string value)
        {
            var v = value.Trim();
            switch (key.Trim().ToLowerInvariant())
            {
                case "rotate":
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rot) || rot % 90 != 0)
                        throw new SettingsException($"rotate must be 0, 90, 180 or 270 (got '{value}')");
                    return s with { Rotate = ((rot % 360) + 360) % 360 };
                case "mirror_horizontal":
                    return s with { MirrorHorizontal = Bool("mirror_horizontal", v) };
                case "mirror_vertical":
                    return s with { MirrorVertical = Bool("mirror_vertical", v) };
                case "units":
                    return s with
                    {
                        Units = v.ToUpperInvariant() switch
                        {
                            "C" or "CELSIUS" or "°C" => TempUnits.C,
                            "F" or "FAHRENHEIT" or "°F" => TempUnits.F,
                            _ => throw new SettingsException($"units must be C or F (got '{value}')"),
                        },
                    };
                case "palette":
                    var p = Palettes.Canonical(v);
                    if (p == null)
                        throw new SettingsException($"palette must be one of {string.Join(", ", Palettes.Names)} (got '{value}')");
                    return s with { Palette = p };
                case "custom_palette":
                    var stops = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (stops.Length < 2 || stops.Length > 5)
                        throw new SettingsException($"custom_palette needs 2 to 5 colours, cold to hot (got {stops.Length})");
                    return s with { CustomPalette = [.. stops.Select(Palettes.NormaliseHex)] };
                case "range":
                    return s with
                    {
                        Range = v.ToLowerInvariant() switch
                        {
                            "auto" => RangeMode.Auto,
                            "locked" or "lock" => RangeMode.Locked,
                            "manual" => RangeMode.Manual,
                            _ => throw new SettingsException($"range must be auto, locked or manual (got '{value}')"),
                        },
                    };
                case "range_min":
                    return s with { RangeMin = Number("range_min", v, s.Units) };
                case "range_max":
                    return s with { RangeMax = Number("range_max", v, s.Units) };
                case "range_speed":
                    return s with
                    {
                        RangeSpeed = v.ToLowerInvariant() switch
                        {
                            "slow" => RangeSpeed.Slow,
                            "normal" or "default" => RangeSpeed.Normal,
                            "fast" => RangeSpeed.Fast,
                            "instant" or "immediate" => RangeSpeed.Instant,
                            _ => throw new SettingsException($"range_speed must be slow, normal, fast or instant (got '{value}')"),
                        },
                    };
                case "auto_shutter":
                    return s with
                    {
                        AutoShutter = v.ToLowerInvariant() switch
                        {
                            "on" or "true" or "yes" or "1" => true,
                            "off" or "false" or "no" or "0" => false,
                            _ => throw new SettingsException($"auto_shutter must be on or off (got '{value}')"),
                        },
                    };
                case "idle_auto_shutter":
                    return s with
                    {
                        IdleAutoShutter = v.ToLowerInvariant() switch
                        {
                            "on" or "true" or "yes" or "1" => true,
                            "off" or "false" or "no" or "0" => false,
                            "unchanged" or "keep" => null,
                            _ => throw new SettingsException($"idle_auto_shutter must be off, on or unchanged (got '{value}')"),
                        },
                    };
                case "overlay":
                    return s with { Overlay = Bool("overlay", v) };
                case "crosshair":
                    return s with { Crosshair = Bool("crosshair", v) };
                case "hotspot":
                    return s with { HotSpot = Bool("hotspot", v) };
                case "readouts":
                    return s with { Readouts = Bool("readouts", v) };
                case "scale":
                    return s with { Scale = Bool("scale", v) };
                case "denoise":
                    return s with { Denoise = Bool("denoise", v) };
                default:
                    throw new SettingsException($"unknown setting '{key}' (known: {string.Join(", ", Keys)})");
            }
        }

        private static bool Bool(string key, string v) => v.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new SettingsException($"{key} must be true or false (got '{v}')"),
        };

        private static double Number(string key, string v, TempUnits units)
        {
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                throw new SettingsException($"{key} must be a number such as 25 or 25.5 (got '{v}')");
            double lo = Temperature.ToUnits(MinC, units), hi = Temperature.ToUnits(MaxC, units);
            if (d < lo || d > hi)
                throw new SettingsException($"{key} must be between {Fmt(lo)} and {Fmt(hi)} {(units == TempUnits.F ? "°F" : "°C")} (got {Fmt(d)})");
            return d;
        }

        private static string Fmt(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>Cross-key rules; null if fine.</summary>
        public static string? Check(WebcamSettings s) =>
            s.RangeMin < s.RangeMax ? null : $"range_min ({Fmt(s.RangeMin)}) must be below range_max ({Fmt(s.RangeMax)})";

        // -- the file ------------------------------------------------------------------------------
        /// <summary>Lenient read of settings.json text: every bad key is listed in
        /// <paramref name="problems"/> and keeps its default.</summary>
        public static WebcamSettings Parse(string json, List<string> problems)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, JsonOptions);
            }
            catch (JsonException e)
            {
                problems.Add($"not valid JSON ({e.Message}); using the defaults");
                return WebcamSettings.Default;
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    problems.Add("the file must hold one JSON object { ... }; using the defaults");
                    return WebcamSettings.Default;
                }
                var values = new Dictionary<string, string>();
                foreach (var prop in root.EnumerateObject())
                {
                    var key = prop.Name.ToLowerInvariant();
                    if (!Keys.Contains(key))
                    {
                        problems.Add($"unknown key '{prop.Name}' ignored (known: {string.Join(", ", Keys)})");
                        continue;
                    }
                    var text = Text(prop.Value);
                    if (text == null)
                        problems.Add($"{key}: unusable value {prop.Value.GetRawText()}; using the default");
                    else
                        values[key] = text;
                }
                // units first: the default manual range is expressed in them
                var units = TempUnits.C;
                if (values.TryGetValue("units", out var u))
                {
                    try
                    {
                        units = With(WebcamSettings.Default, "units", u).Units;
                    }
                    catch (SettingsException e)
                    {
                        problems.Add(e.Message + "; using C");
                    }
                    values.Remove("units");
                }
                var s = WebcamSettings.DefaultsFor(units);
                foreach (var key in Keys)
                {
                    if (!values.TryGetValue(key, out var text))
                        continue;
                    try
                    {
                        s = With(s, key, text);
                    }
                    catch (SettingsException e)
                    {
                        problems.Add(e.Message + "; using the default");
                    }
                }
                if (Check(s) is { } bad)
                {
                    var d = WebcamSettings.DefaultsFor(units);
                    problems.Add($"{bad}; using {Fmt(d.RangeMin)} .. {Fmt(d.RangeMax)}");
                    s = s with { RangeMin = d.RangeMin, RangeMax = d.RangeMax };
                }
                return s;
            }
        }

        private static string? Text(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array when e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String) =>
                string.Join(",", e.EnumerateArray().Select(x => x.GetString())),
            _ => null,
        };

        /// <summary>Strict update for "settings set key=value ...": SettingsException (nothing changed)
        /// unless every assignment is valid. Changing units without giving range_min / range_max in
        /// the same command converts the manual range to the new units (rounded to 0.1).</summary>
        public static WebcamSettings Apply(WebcamSettings current, IEnumerable<string> assignments)
        {
            var errors = new List<string>();
            var pairs = new List<(string Key, string Value)>();
            foreach (var a in assignments)
            {
                var eq = a.IndexOf('=');
                if (eq <= 0)
                {
                    errors.Add($"'{a}' is not key=value");
                    continue;
                }
                pairs.Add((a[..eq].Trim().ToLowerInvariant(), a[(eq + 1)..].Trim()));
            }
            if (pairs.Count == 0 && errors.Count == 0)
                errors.Add("nothing to set (give key=value pairs)");
            var s = current;
            // units first, so range_min / range_max in the same command are read in the new units
            foreach (var (key, value) in pairs.Where(p => p.Key == "units").Concat(pairs.Where(p => p.Key != "units")))
            {
                try
                {
                    var before = s.Units;
                    s = With(s, key, value);
                    if (key == "units" && s.Units != before && !pairs.Any(p => p.Key is "range_min" or "range_max"))
                    {
                        s = s with
                        {
                            RangeMin = Math.Round(Temperature.ToUnits(Temperature.ToCelsius(s.RangeMin, before), s.Units), 1),
                            RangeMax = Math.Round(Temperature.ToUnits(Temperature.ToCelsius(s.RangeMax, before), s.Units), 1),
                        };
                    }
                }
                catch (SettingsException e)
                {
                    errors.Add(e.Message);
                }
            }
            if (errors.Count == 0 && Check(s) is { } bad)
                errors.Add(bad);
            if (errors.Count > 0)
                throw new SettingsException(string.Join("\n", errors));
            return s;
        }

        /// <summary>The canonical settings.json text (every key, two-space indent, LF line ends).</summary>
        public static string ToJson(WebcamSettings s)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
            {
                w.WriteStartObject();
                w.WriteNumber("rotate", s.Rotate);
                w.WriteBoolean("mirror_horizontal", s.MirrorHorizontal);
                w.WriteBoolean("mirror_vertical", s.MirrorVertical);
                w.WriteString("units", s.Units.ToString());
                w.WriteString("palette", s.Palette);
                w.WriteStartArray("custom_palette");
                foreach (var c in s.CustomPalette)
                    w.WriteStringValue(c);
                w.WriteEndArray();
                w.WriteString("range", s.Range.ToString().ToLowerInvariant());
                w.WriteNumber("range_min", s.RangeMin);
                w.WriteNumber("range_max", s.RangeMax);
                w.WriteString("range_speed", s.RangeSpeed.ToString().ToLowerInvariant());
                w.WriteString("auto_shutter", s.AutoShutter ? "on" : "off");
                w.WriteString("idle_auto_shutter", IdleText(s.IdleAutoShutter));
                w.WriteBoolean("overlay", s.Overlay);
                w.WriteBoolean("crosshair", s.Crosshair);
                w.WriteBoolean("hotspot", s.HotSpot);
                w.WriteBoolean("readouts", s.Readouts);
                w.WriteBoolean("scale", s.Scale);
                w.WriteBoolean("denoise", s.Denoise);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
        }

        public static string IdleText(bool? idle) => idle switch { true => "on", false => "off", null => "unchanged" };

        /// <summary>Reads the file (lenient). A missing file gives the defaults with no problems.
        /// Throws IOException / UnauthorizedAccessException if it exists but cannot be read.</summary>
        public static WebcamSettings Load(string path, List<string> problems)
        {
            if (!File.Exists(path))
                return WebcamSettings.Default;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(sr.ReadToEnd(), problems);
        }

        /// <summary>Writes the file atomically (temporary file, then replace), UTF-8 without BOM.</summary>
        public static void Save(string path, WebcamSettings s)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, new UTF8Encoding(false).GetBytes(ToJson(s)));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 10)
                {
                    Thread.Sleep(20); // Windows refuses to replace a file another handle has open (the camera reading it)
                }
            }
        }
    }

    /// <summary>
    /// The current settings for the media source: <see cref="Poll"/> re-reads settings.json when its
    /// modification time or size changed (at most every PollS seconds; <c>force</c> reads it now, as at
    /// stream start). <see cref="Version"/> grows on every change. Never throws.
    /// </summary>
    public sealed class SettingsWatcher(string? path, IClock clock, double pollS = 1.0)
    {
        private readonly object _lock = new();
        private (bool Exists, DateTime Mtime, long Length)? _stamp;
        private double _nextPoll = double.NegativeInfinity;

        public string? Path { get; } = path;
        public WebcamSettings Current { get; private set; } = WebcamSettings.Default;
        public long Version { get; private set; }
        public IReadOnlyList<string> Problems { get; private set; } = [];

        /// <summary>True if the settings changed.</summary>
        public bool Poll(bool force = false)
        {
            if (Path == null)
                return false;
            lock (_lock)
            {
                var mono = clock.Monotonic;
                if (!force && mono < _nextPoll)
                    return false;
                _nextPoll = mono + pollS;
                (bool, DateTime, long) stamp;
                try
                {
                    var info = new FileInfo(Path);
                    stamp = info.Exists ? (true, info.LastWriteTimeUtc, info.Length) : (false, default, 0);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
                if (_stamp == stamp)
                    return false;
                var problems = new List<string>();
                WebcamSettings s;
                try
                {
                    s = SettingsFile.Load(Path, problems);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // being replaced, or not readable: keep what we have and try again next poll
                    Log.Warn($"cannot read {Path}: {e.Message}");
                    return false;
                }
                var first = _stamp == null;
                _stamp = stamp;
                Problems = problems;
                foreach (var p in problems)
                    Log.Warn($"settings.json: {p}");
                if (!first || stamp.Item1)
                    Log.Info(stamp.Item1 ? $"settings: {Describe(s)}" : "settings.json removed: using the defaults");
                var changed = !Same(s, Current);
                Current = s;
                if (changed)
                    Version++;
                return changed;
            }
        }

        public static string Describe(WebcamSettings s) =>
            $"rotate {s.Rotate}, mirror h {s.MirrorHorizontal} v {s.MirrorVertical}, units {s.Units}, palette {s.Palette}" +
            (s.Palette == "custom" ? $" ({string.Join(" ", s.CustomPalette)})" : "") +
            $", range {s.Range.ToString().ToLowerInvariant()}" +
            (s.Range == RangeMode.Auto && s.RangeSpeed != RangeSpeed.Normal ? $" ({s.RangeSpeed.ToString().ToLowerInvariant()})" : "") +
            (s.Range == RangeMode.Manual ? $" {s.RangeMin.ToString(CultureInfo.InvariantCulture)} .. {s.RangeMax.ToString(CultureInfo.InvariantCulture)}" : "") +
            $", auto shutter {(s.AutoShutter ? "on" : "off")}, idle {SettingsFile.IdleText(s.IdleAutoShutter)}" +
            (s.Overlay ? Hidden(s) : ", overlay off") +
            (s.Denoise ? ", denoise on" : "");

        private static string Hidden(WebcamSettings s)
        {
            var off = new List<string>();
            if (!s.Crosshair) off.Add("crosshair");
            if (!s.HotSpot) off.Add("hotspot");
            if (!s.Readouts) off.Add("readouts");
            if (!s.Scale) off.Add("scale");
            return off.Count == 0 ? "" : $", hidden: {string.Join(" ", off)}";
        }

        /// <summary>Value equality (the record compares the colour array by reference).</summary>
        public static bool Same(WebcamSettings a, WebcamSettings b) =>
            a with { CustomPalette = NoColours } == b with { CustomPalette = NoColours } && a.CustomPalette.SequenceEqual(b.CustomPalette);

        private static readonly string[] NoColours = [];
    }
}
