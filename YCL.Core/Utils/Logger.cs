using System;
using System.IO;
using System.Threading;

namespace YCL.Core.Utils
{
    /// <summary>
    /// 全局日志工具。提供静态方法 <see cref="Debug"/>, <see cref="Info"/>,
    /// <see cref="Warn"/>, <see cref="Error"/>，方便在任何地方调用。
    /// 日志按日期分文件保存到 %AppData%\YCL\logs\ 目录下，
    /// 文件名形如 log_2026-07-17.txt。所有写入操作都是线程安全的。
    /// </summary>
    public static class Logger
    {
        /// <summary>日志级别</summary>
        private enum Level
        {
            DEBUG,
            INFO,
            WARN,
            ERROR
        }

        // 同一时刻只允许一个线程写文件，保证多线程下日志不会错乱
        private static readonly object _lock = new();

        // 日志目录：%AppData%\YCL\logs\
        private static readonly string _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YCL", "logs");

        /// <summary>
        /// 返回今天对应的日志文件完整路径。
        /// 每天会自动换一个新文件，方便按日期查找。
        /// </summary>
        private static string GetTodayLogFile()
        {
            var fileName = $"log_{DateTime.Now:yyyy-MM-dd}.txt";
            return Path.Combine(_logDirectory, fileName);
        }

        /// <summary>把一行日志写入文件</summary>
        private static void Write(Level level, string message, Exception? exception)
        {
            // 组装日志内容：时间 | 线程ID | 级别 | 消息
            var time = DateTime.Now.ToString("HH:mm:ss.fff");
            var threadId = Thread.CurrentThread.ManagedThreadId;
            var line = $"[{time}] [T{threadId}] [{level}] {message}";

            // 如果有异常，把异常的详细信息也写进去（包含堆栈，方便排查问题）
            if (exception != null)
            {
                line += Environment.NewLine + "  → 异常类型: " + exception.GetType().FullName
                     + Environment.NewLine + "  → 异常消息: " + exception.Message
                     + Environment.NewLine + "  → 堆栈跟踪:" + Environment.NewLine + exception.StackTrace;
            }

            // 加锁保证线程安全
            lock (_lock)
            {
                try
                {
                    // 目录不存在则创建（已存在不会报错）
                    Directory.CreateDirectory(_logDirectory);
                    // 追加写入到今天的日志文件末尾
                    File.AppendAllText(GetTodayLogFile(), line + Environment.NewLine);
                }
                catch
                {
                    // 日志本身不能再抛出异常，否则会让程序崩溃
                    // 这里静默吞掉所有写入错误
                }
            }
        }

        /// <summary>输出 DEBUG 级别日志（最详细，通常只在调试时用）</summary>
        public static void Debug(string message) => Write(Level.DEBUG, message, null);

        /// <summary>输出 INFO 级别日志（常规信息，比如"启动器已启动"）</summary>
        public static void Info(string message) => Write(Level.INFO, message, null);

        /// <summary>输出 WARN 级别日志（警告，程序还能继续运行，但需要留意）</summary>
        public static void Warn(string message) => Write(Level.WARN, message, null);

        /// <summary>输出 ERROR 级别日志（错误，可以附带异常对象）</summary>
        public static void Error(string message, Exception? exception = null)
            => Write(Level.ERROR, message, exception);
    }
}
