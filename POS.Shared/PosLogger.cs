using System;
using System.IO;

namespace POS.Shared
{
    public enum LogLevel
    {
        INFO,
        WARN,
        ERROR,
        DEBUG
    }

    public class PosLogger
    {
        private static readonly object _lock = new object();
        private static string _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "POS-SIM", "Logs");

        public static string Source { get; set; } = "SYSTEM";

        public static void Log(LogLevel level, string module, string message)
        {
            // Sanitize message - replace newlines with space
            string clean = message.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{Source}] [{module}] {clean}";

            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(_logDirectory);
                    string filename = Path.Combine(_logDirectory, $"pos_{DateTime.Now:yyyy-MM-dd}.log");
                    File.AppendAllText(filename, entry + Environment.NewLine);
                }
                catch { }
            }

            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                LogLevel.INFO => ConsoleColor.White,
                LogLevel.WARN => ConsoleColor.Yellow,
                LogLevel.ERROR => ConsoleColor.Red,
                LogLevel.DEBUG => ConsoleColor.Gray,
                _ => ConsoleColor.White
            };
            Console.WriteLine(entry);
            Console.ForegroundColor = originalColor;
        }

        // Convenience methods with module
        public static void Info(string module, string message) => Log(LogLevel.INFO, module, message);
        public static void Warn(string module, string message) => Log(LogLevel.WARN, module, message);
        public static void Error(string module, string message) => Log(LogLevel.ERROR, module, message);
        public static void Debug(string module, string message) => Log(LogLevel.DEBUG, module, message);

        // Backwards compat - no module defaults to GENERAL
        public static void Info(string message) => Log(LogLevel.INFO, "GENERAL", message);
        public static void Warn(string message) => Log(LogLevel.WARN, "GENERAL", message);
        public static void Error(string message) => Log(LogLevel.ERROR, "GENERAL", message);
        public static void Debug(string message) => Log(LogLevel.DEBUG, "GENERAL", message);

        public static string GetLogPath() => _logDirectory;
    }
}