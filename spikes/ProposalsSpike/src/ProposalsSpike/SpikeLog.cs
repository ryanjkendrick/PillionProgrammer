using System;
using System.IO;
using System.Threading;

namespace ProposalsSpike
{
    /// <summary>
    /// Append-only log at %TEMP%\ProposalsSpike.log so behaviour can be verified without looking at the screen.
    /// </summary>
    internal static class SpikeLog
    {
        private static readonly object Gate = new object();

        public static readonly string FilePath = Path.Combine(Path.GetTempPath(), "ProposalsSpike.log");

        public static void Write(string area, string message)
        {
            try
            {
                bool ui = System.Windows.Application.Current?.Dispatcher?.CheckAccess() ?? false;
                string line = $"{DateTime.Now:HH:mm:ss.fff} [T{Thread.CurrentThread.ManagedThreadId:D2}{(ui ? " UI" : "")}] {area,-10} {message}{Environment.NewLine}";
                lock (Gate)
                {
                    File.AppendAllText(FilePath, line);
                }
            }
            catch
            {
                // Logging must never break the editor.
            }
        }

        /// <summary>Make whitespace and newlines visible in log lines.</summary>
        public static string Show(string s)
        {
            if (s == null) return "<null>";
            return "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }

        /// <summary>Show a trigger character as its code point plus the glyph when printable.</summary>
        public static string Show(char c) => $"U+{(int)c:X4}" + (c > ' ' ? "'" + c + "'" : "");
    }
}
