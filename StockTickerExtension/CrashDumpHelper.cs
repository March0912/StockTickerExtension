using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace StockTickerExtension
{
    public static class CrashDumpHelper
    {
        public static void RegisterExtensionOnlyHandlers()
        {
            // 捕获你扩展代码中的未处理异常（UI 线程/WPF）
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (IsFromThisExtension(e.ExceptionObject))
                {
                    WriteDump("AppDomain_UnhandledException");
                }
            };

            // 捕获 WPF Dispatcher 未处理异常（也属于你扩展 GUI）
            System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (s, e) =>
            {
                if (IsFromThisExtension(e.Exception))
                {
                    WriteDump("Dispatcher_UnhandledException");
                }
            };

            // 捕获 Task.Run / async void 内的未观察异常
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                if (IsFromThisExtension(e.Exception))
                {
                    WriteDump("TaskScheduler_UnobservedTaskException");
                }
            };
        }

        // 判断异常是否来自你的扩展程序集
        private static bool IsFromThisExtension(object ex)
        {
            if (ex is Exception exception)
            {
                var st = exception.StackTrace;
                if (st == null) return false;

                // 判断堆栈是否包含你的插件命名空间（修改成你扩展的根命名空间）
                return st.Contains("StockTickerExtension");
            }
            return false;
        }

        private static void WriteDump(string reason)
        {
            try
            {
                string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StockWatcher", "dump");
                Directory.CreateDirectory(dumpDir);
                string file = Path.Combine(dumpDir, $"ExtensionCrash_{reason}_{DateTime.Now:yyyyMMdd_HHmmss}.dmp");

                using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    Process process = Process.GetCurrentProcess();
                    MiniDumpWriteDump(process.Handle, 
                        process.Id, 
                        fs.SafeFileHandle.DangerousGetHandle(), 
                        MINIDUMP_TYPE.MiniDumpWithFullMemory, 
                        IntPtr.Zero, 
                        IntPtr.Zero, 
                        IntPtr.Zero);
                }
            }
            catch
            {
                // 避免 dump 写入期间再次导致崩溃
            }
        }

        #region Win32
        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(
            IntPtr hProcess,
            int processId,
            IntPtr hFile,
            MINIDUMP_TYPE dumpType,
            IntPtr expParam,
            IntPtr userStreamParam,
            IntPtr callbackParam
        );

        private enum MINIDUMP_TYPE : int
        {
            MiniDumpNormal = 0x00000000,
            MiniDumpWithDataSegs = 0x00000001,
            MiniDumpWithFullMemory = 0x00000002,
            MiniDumpWithHandleData = 0x00000004,
            MiniDumpWithThreadInfo = 0x00001000,
        }
        #endregion
    }
}