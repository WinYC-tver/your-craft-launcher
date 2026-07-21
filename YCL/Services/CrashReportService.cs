using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using YCL.Core.Utils;

namespace YCL.Services
{
    /// <summary>
    /// 游戏崩溃报告服务（静态，HMCL 风格）。
    ///
    /// 当游戏进程异常退出（退出码 != 0）时，调用 <see cref="GenerateCrashReport"/>
    /// 在启动器文件夹下的 <c>clog</c> 文件夹中生成一份文本报告，
    /// 包含时间、版本、退出码、Java 路径、游戏目录、系统信息（OS/.NET/内存）、
    /// 游戏日志摘录（最后 100 行）以及崩溃分析与建议。
    ///
    /// 路径通过 <see cref="AppPaths.CrashLogsDirectory"/> 获取，支持便携模式。
    /// 采用静态类 + 静态方法，避免在 DI 容器中注册（不需要修改 App.xaml.cs）。
    /// </summary>
    public static class CrashReportService
    {
        /// <summary>日志摘录保留的最大行数（HMCL 风格：尾部 100 行）</summary>
        private const int LogTailLines = 100;

        /// <summary>
        /// 生成崩溃报告文件。
        ///
        /// 文件位置：<c>&lt;启动器文件夹&gt;/clog/crash-yyyyMMdd-HHmmss.txt</c>
        /// （路径由 <see cref="AppPaths.CrashLogsDirectory"/> 提供，目录不存在会自动创建）。
        /// </summary>
        /// <param name="versionId">崩溃的游戏版本 id</param>
        /// <param name="exitCode">游戏进程退出码</param>
        /// <param name="gameLog">游戏完整日志（多行，已包含换行）</param>
        /// <param name="minecraftPath">.minecraft 游戏目录路径</param>
        /// <param name="javaPath">使用的 Java 路径（javaw.exe 路径）</param>
        /// <returns>生成的报告文件完整路径；失败返回 null</returns>
        public static string? GenerateCrashReport(
            string versionId,
            int exitCode,
            string gameLog,
            string minecraftPath,
            string javaPath)
        {
            try
            {
                // 报告目录：通过 AppPaths 集中管理（便携模式兼容）
                var reportDir = AppPaths.CrashLogsDirectory;

                // 文件名：crash-yyyyMMdd-HHmmss.txt（HMCL 风格，仅含时间戳）
                var fileName = $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
                var reportPath = Path.Combine(reportDir, fileName);

                // 构建报告内容
                var sb = new StringBuilder();
                sb.AppendLine("========================================");
                sb.AppendLine("  YCL 游戏崩溃报告");
                sb.AppendLine("========================================");
                sb.AppendLine();
                sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"版本：{versionId}");
                sb.AppendLine($"退出码：{exitCode}（0=正常退出，非 0=异常退出）");
                sb.AppendLine($"Java 路径：{javaPath}");
                sb.AppendLine($"游戏目录：{minecraftPath}");
                sb.AppendLine();

                // 系统信息
                AppendSystemInfo(sb);
                sb.AppendLine();

                // 崩溃分析与建议
                sb.AppendLine("------ 崩溃分析与建议 ------");
                var analysis = AnalyzeCrash(gameLog);
                if (analysis.Count == 0)
                {
                    sb.AppendLine("未匹配到已知崩溃模式，请查看日志摘录自行排查。");
                }
                else
                {
                    foreach (var line in analysis)
                    {
                        sb.AppendLine("- " + line);
                    }
                }
                sb.AppendLine();

                // 游戏日志摘录（最后 100 行）
                sb.AppendLine($"------ 游戏日志摘录（最后 {LogTailLines} 行）------");
                sb.Append(GetLogTail(gameLog, LogTailLines));
                sb.AppendLine();

                sb.AppendLine("========================================");
                sb.AppendLine("  报告结束");
                sb.AppendLine("========================================");

                // 写入文件
                File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);

                Logger.Info($"崩溃报告已生成：{reportPath}");
                return reportPath;
            }
            catch (Exception ex)
            {
                Logger.Error("生成崩溃报告失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 根据日志关键字分析常见崩溃原因，返回对应的解决建议列表。
        /// 命中多条时全部返回。
        /// v26.1.0.5：改为 public，让 LaunchPageViewModel 在全屏崩溃提示中直接调用显示建议。
        /// </summary>
        public static List<string> AnalyzeCrash(string gameLog)
        {
            var tips = new List<string>();
            if (string.IsNullOrEmpty(gameLog))
                return tips;

            // 关键字匹配（不区分大小写）
            var log = gameLog;
            if (log.Contains("java.lang.OutOfMemoryError", StringComparison.OrdinalIgnoreCase))
            {
                tips.Add("检测到内存不足错误（java.lang.OutOfMemoryError）。建议：在设置中增大最大内存，或关闭其他占用内存的程序。");
            }
            if (log.Contains("Could not find or load main class", StringComparison.OrdinalIgnoreCase))
            {
                tips.Add("检测到主类未找到（Could not find or load main class）。建议：版本文件可能损坏，请重新下载该版本。");
            }
            if (log.Contains("UnsatisfiedLinkError", StringComparison.OrdinalIgnoreCase))
            {
                tips.Add("检测到 native 库加载失败（UnsatisfiedLinkError）。建议：缺少 native 库，请重新下载版本或检查架构是否匹配（32/64 位）。");
            }
            if (log.Contains("OpenGL", StringComparison.OrdinalIgnoreCase))
            {
                tips.Add("检测到 OpenGL 相关错误。建议：显卡驱动可能过旧或不兼容，请更新显卡驱动到最新版。");
            }
            // 同时包含 Mod 与 Exception 才认为是模组冲突
            if (log.Contains("Mod", StringComparison.OrdinalIgnoreCase)
                && log.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            {
                tips.Add("检测到模组相关异常。建议：检查模组兼容性（版本、加载器类型、依赖），尝试逐个禁用模组定位冲突源。");
            }

            return tips;
        }

        /// <summary>
        /// 把系统信息追加到报告 <see cref="StringBuilder"/>。
        /// 包含 OS 版本、.NET 版本、处理器核数、机器名、物理内存。
        /// </summary>
        private static void AppendSystemInfo(StringBuilder sb)
        {
            sb.AppendLine("------ 系统信息 ------");
            sb.AppendLine($"操作系统：{Environment.OSVersion}");
            sb.AppendLine($"系统 64 位：{Environment.Is64BitOperatingSystem}");
            sb.AppendLine($"进程 64 位：{Environment.Is64BitProcess}");
            sb.AppendLine($"处理器核数：{Environment.ProcessorCount}");
            sb.AppendLine($"机器名：{Environment.MachineName}");
            sb.AppendLine($".NET CLR 版本：{Environment.Version}");
            sb.AppendLine($".NET 运行时版本：{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

            // 内存信息：用 GlobalMemoryStatusEx 读取物理内存（不依赖性能计数器，无需特殊权限）
            try
            {
                if (TryGetMemoryStatus(out var totalMb, out var availMb))
                {
                    sb.AppendLine($"总物理内存：{totalMb:F0} MB");
                    sb.AppendLine($"可用物理内存：{availMb:F0} MB");
                }
                else
                {
                    sb.AppendLine("总物理内存：读取失败");
                    sb.AppendLine("可用物理内存：读取失败");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"内存信息：读取失败（{ex.Message}）");
            }

            // 当前进程内存占用
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                sb.AppendLine($"启动器进程工作集：{proc.WorkingSet64 / (1024 * 1024)} MB");
            }
            catch
            {
                // 读取失败不阻塞报告生成
            }
        }

        /// <summary>
        /// 从完整日志中截取最后 <paramref name="maxLines"/> 行，保留原始换行。
        /// 行数不足时返回全部内容；空内容返回占位符。
        /// </summary>
        private static string GetLogTail(string? gameLog, int maxLines)
        {
            if (string.IsNullOrEmpty(gameLog))
                return "(无日志)" + Environment.NewLine;

            // 按行分割（保留所有平台换行符兼容性）
            var lines = gameLog.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            if (lines.Length <= maxLines)
                return gameLog + Environment.NewLine;

            var tail = lines.Skip(lines.Length - maxLines).ToArray();
            return string.Join(Environment.NewLine, tail) + Environment.NewLine;
        }

        /// <summary>
        /// 调用 Windows API GlobalMemoryStatusEx 读取物理内存状态。
        /// 成功返回 true 并输出总内存/可用内存（单位 MB）。
        /// </summary>
        private static bool TryGetMemoryStatus(out double totalMb, out double availableMb)
        {
            totalMb = 0;
            availableMb = 0;
            try
            {
                var status = new MEMORYSTATUSEX
                {
                    dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
                };
                if (!GlobalMemoryStatusEx(status))
                    return false;
                totalMb = status.ullTotalPhys / (1024.0 * 1024.0);
                availableMb = status.ullAvailPhys / (1024.0 * 1024.0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
    }
}
