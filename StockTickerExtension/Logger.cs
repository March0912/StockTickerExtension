using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace StockTickerExtension
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static readonly string _logDir;
        private static readonly string _logFile;

        static Logger()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _logDir = Path.Combine(appData, "StockWatcher", "Logs");

            if (!Directory.Exists(_logDir))
                Directory.CreateDirectory(_logDir);

            _logFile = Path.Combine(_logDir, $"log_{DateTime.Now:yyyyMMdd}.txt");

            CleanupOldLogs();
        }

        private static void CleanupOldLogs()
        {
            try
            {
                foreach (var file in Directory.GetFiles(_logDir, "log_*.txt"))
                {
                    var creationTime = File.GetCreationTime(file);
                    if ((DateTime.Now - creationTime).TotalDays > 10)
                        File.Delete(file);
                }
            }
            catch
            {
                // 忽略清理异常
            }
        }

        public static void Info(string message,
            [CallerFilePath] string file = "",
            [CallerMemberName] string member = "",
            [CallerLineNumber] int line = 0)
            => WriteLog("INFO", message, file, member, line);

        public static void Error(string message,
            [CallerFilePath] string file = "",
            [CallerMemberName] string member = "",
            [CallerLineNumber] int line = 0)
            => WriteLog("ERROR", message, file, member, line);

        public static void Debug(string message,
            [CallerFilePath] string file = "",
            [CallerMemberName] string member = "",
            [CallerLineNumber] int line = 0)
            => WriteLog("DEBUG", message, file, member, line);

        private static void WriteLog(string level, string message, string file, string member, int line)
        {
            string fileName = Path.GetFileName(file);
            string logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{fileName}:{line}] {member}() - {message}";

            Task.Run(() =>
            {
                try
                {
                    lock (_lock)
                    {
                        File.AppendAllText(_logFile, logLine + Environment.NewLine, Encoding.UTF8);
                    }
                }
                catch
                {
                    // 忽略写入异常
                }
            });
        }
    }

}
