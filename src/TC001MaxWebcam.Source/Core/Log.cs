// TC001 Max Thermal Webcam: where Core writes its log lines (the media source routes them to ETW and
// to webcam.log; the self-test collects them).

using System;
using System.Globalization;

namespace TC001MaxWebcam.Core
{
    public static class Log
    {
        public static Action<string, string>? Sink;

        public static void Info(string message) => Sink?.Invoke("info", message);
        public static void Warn(string message) => Sink?.Invoke("warn", message);
        public static void Error(string message) => Sink?.Invoke("error", message);

        public static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }
}
