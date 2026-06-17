using System;
using System.IO;

namespace BgsDataBridge
{
    /// <summary>
    /// Minimal file logger for the BgsDataBridge plugin. Writes to
    /// &lt;pluginDir&gt;/log.txt with ISO-8601 timestamps. Every write is
    /// wrapped so a logging failure can never crash the plugin or the game
    /// thread (OnUpdate runs there). Verified by compilation + runtime at
    /// Task 12; there are no unit tests for this integration layer.
    /// </summary>
    public static class Logger
    {
        private static string _dir;

        public static void Init(string dir)
        {
            _dir = dir;
            try { Directory.CreateDirectory(dir); }
            catch { /* non-fatal: logging is best-effort */ }
        }

        public static void Info(string m) => Write("INFO", m);
        public static void Error(string m) => Write("ERR ", m);

        static void Write(string lvl, string m)
        {
            if (_dir == null) return;
            try
            {
                var path = Path.Combine(_dir, "log.txt");
                File.AppendAllText(path, DateTime.Now.ToString("o") + " " + lvl + " " + m + Environment.NewLine);
            }
            catch { /* never throw out of the logger */ }
        }
    }
}
