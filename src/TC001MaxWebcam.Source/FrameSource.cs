// TC001 Max Thermal Webcam: produces the camera's frames.
//
// Replaces VCamNetSample's FrameGenerator.cs (Copyright (c) 2024-2026 Simon Mourier, MIT License,
// see THIRD_PARTY_NOTICES.txt). This project's own code: the picture comes from the WebcamEngine
// (our own TC001 Max reader and settings.json) and is drawn on the CPU by FrameRenderer; RGB32 is
// converted to NV12 here (BT.601, limited range) instead of through the Color Converter DSP.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DirectN;
using TC001MaxWebcam.Core;
using TC001MaxWebcam.Usb;
using TC001MaxWebcam.Utilities;

namespace TC001MaxWebcam
{
    public sealed class FrameSource : IDisposable
    {
        public const uint Width = Shared.Width;
        public const uint Height = Shared.Height;
        public const int RgbSize = (int)(Width * Height * 4);
        public const int Nv12Size = (int)(Width * Height * 3 / 2);

        private readonly byte[] _bgra = new byte[RgbSize];   // current output frame
        private readonly byte[] _nv12 = new byte[Nv12Size];
        private readonly FrameRenderer _renderer = new();
        private WebcamEngine? _engine;
        private ulong _frameCount;
        private long _shownVersion = long.MinValue;
        private bool _nv12Valid;

        static FrameSource()
        {
            Log.Sink = WebcamLog.Write;
        }

        public ulong FrameCount => _frameCount;

        /// <summary>A stream started: run the engine (it reads settings.json and opens the camera).</summary>
        public void Start()
        {
            if (_engine != null)
                return;
            try
            {
                _renderer.ResetRange();
                _shownVersion = long.MinValue;
                _engine = WebcamEngine.Acquire(() => new EngineOptions
                {
                    LinkFactory = () => new WinUsbLink(),
                    SettingsPath = Shared.SettingsPath,
                    ClaimDir = Shared.ClaimDir,   // take turns with labbench (Core/CameraClaims.cs)
                });
            }
            catch (Exception e)
            {
                EventProvider.LogError("starting the engine failed: " + e);
            }
        }

        /// <summary>The stream stopped: the engine closes the camera when no stream is left.</summary>
        public void Stop()
        {
            if (_engine == null)
                return;
            _engine = null;
            WebcamEngine.Release();
        }

        /// <summary>Draws the newest picture into the output buffer (unchanged pictures are not redrawn).</summary>
        private void Update()
        {
            Picture pic;
            try
            {
                pic = _engine?.Current() ?? Picture.Card(-1, FrameRenderer.Footer, "Starting...");
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                pic = Picture.Card(-2, FrameRenderer.Footer, "Internal error: " + e.Message);
            }
            if (pic.Version == _shownVersion)
                return;
            _renderer.Render(pic, _bgra);
            _shownVersion = pic.Version;
            _nv12Valid = false;
        }

        /// <summary>Fills the allocator's sample with the current frame in the negotiated format.</summary>
        public void WriteSample(IComObject<IMFSample> sample, Guid format)
        {
            Update();
            using var buffer = sample.GetBufferByIndex(0);
            if (format == MFConstants.MFVideoFormat_NV12)
            {
                if (!_nv12Valid)
                {
                    BgraToNv12(_bgra, (int)Width, (int)Height, _nv12);
                    _nv12Valid = true;
                }
                buffer.WithLock((scanline, length, _) => Marshal.Copy(_nv12, 0, scanline, (int)Math.Min((long)length, Nv12Size)));
                buffer.SetCurrentLength(Nv12Size);
            }
            else
            {
                buffer.WithLock((scanline, length, _) => Marshal.Copy(_bgra, 0, scanline, (int)Math.Min((long)length, RgbSize)));
                buffer.SetCurrentLength(RgbSize);
            }
            _frameCount++;
        }

        /// <summary>BGRA to NV12 (BT.601, limited range: Y 16..235, Cb/Cr 16..240), 2 x 2 chroma average.</summary>
        public static void BgraToNv12(byte[] bgra, int width, int height, byte[] nv12)
        {
            for (var y = 0; y < height; y++)
            {
                var row = y * width * 4;
                var yrow = y * width;
                for (var x = 0; x < width; x++)
                {
                    var i = row + x * 4;
                    int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                    nv12[yrow + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                }
            }

            var uv = width * height;
            for (var y = 0; y < height; y += 2)
            {
                for (var x = 0; x < width; x += 2)
                {
                    int r = 0, g = 0, b = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var i = ((y + dy) * width + x + dx) * 4;
                            b += bgra[i];
                            g += bgra[i + 1];
                            r += bgra[i + 2];
                        }
                    }
                    r = (r + 2) >> 2;
                    g = (g + 2) >> 2;
                    b = (b + 2) >> 2;
                    var o = uv + (y / 2) * width + x;
                    nv12[o] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    nv12[o + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
        }

        public void Dispose()
        {
            EventProvider.LogInfo();
            Stop();
        }
    }

    /// <summary>Core's log lines go to ETW (Shared.EtwProviderId) and to webcam.log in the data folder
    /// (rotated at Shared.LogMaxBytes; silently skipped for a minute if not writable).</summary>
    internal static class WebcamLog
    {
        private static readonly object Lock = new();
        private static long _retryAfter;

        public static void Write(string level, string message)
        {
            if (level == "error")
                EventProvider.LogError(message, "Core");
            else if (level == "warn")
                EventProvider.LogWarning(message, "Core");
            else
                EventProvider.LogInfo(message, "Core");

            lock (Lock)
            {
                if (Environment.TickCount64 < _retryAfter)
                    return;
                try
                {
                    var path = Path.Combine(Shared.DataDir, Shared.LogName);
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > Shared.LogMaxBytes)
                        File.Move(path, path + ".1", overwrite: true);
                    var line = $"{Log.Stamp()} [{Environment.ProcessId}] {level,-5} {message}\r\n";
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = Encoding.UTF8.GetBytes(line);
                    fs.Write(bytes, 0, bytes.Length);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _retryAfter = Environment.TickCount64 + 60_000;
                }
            }
        }
    }
}
