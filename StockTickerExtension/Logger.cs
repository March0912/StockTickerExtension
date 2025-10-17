using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace StockTickerExtension
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logDir;
        private static string _logFile;

        static Logger()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _logDir = Path.Combine(appData, "StockWatcher", "Logs");

            if (!Directory.Exists(_logDir))
                Directory.CreateDirectory(_logDir);

            _logFile = Path.Combine(_logDir, $"log_{DateTime.Now:yyyyMMdd}.txt");

            // 删除7天前的日志
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
            catch { /* 忽略清理异常 */ }
        }

        public static void Info(string message) => WriteLog("INFO", message);
        public static void Error(string message) => WriteLog("ERROR", message);
        public static void Debug(string message) => WriteLog("DEBUG", message);

        private static void WriteLog(string level, string message)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            Task.Run(() =>
            {
                try
                {
                    lock (_lock)
                    {
                        File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8);
                    }
                }
                catch { /* 忽略写入异常 */ }
            });
        }
    }
}
