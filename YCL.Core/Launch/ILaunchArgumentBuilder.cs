using System.Collections.Generic;
using YCL.Models.Accounts;
using YCL.Core.Versions;

namespace YCL.Core.Launch
{
    /// <summary>
    /// 启动参数生成结果。包含 java 路径、JVM 参数、主类、游戏参数四部分，
    /// 调用方可以直接拼装成完整命令行，也可分别使用。
    /// </summary>
    public class LaunchArguments
    {
        /// <summary>java 可执行文件路径</summary>
        public string JavaPath { get; set; } = string.Empty;

        /// <summary>JVM 参数列表（如 -Xmx2G、-cp、-D... 等）</summary>
        public List<string> JvmArguments { get; set; } = new();

        /// <summary>主类全名（如 net.minecraft.client.main.Main）</summary>
        public string MainClass { get; set; } = string.Empty;

        /// <summary>游戏参数列表（如 --username、Steve 等）</summary>
        public List<string> GameArguments { get; set; } = new();

        /// <summary>游戏工作目录（.minecraft 或版本隔离目录）</summary>
        public string WorkingDirectory { get; set; } = string.Empty;

        /// <summary>natives 解压目录的绝对路径</summary>
        public string NativesDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 把所有参数拼成单个命令行字符串（用于日志输出）。
        /// 注意：实际启动进程时应该用参数列表传给 ProcessStartInfo，
        /// 避免路径含空格导致解析错误。
        /// </summary>
        public string ToCommandLine()
        {
            var parts = new List<string> { JavaPath };
            parts.AddRange(JvmArguments);
            parts.Add(MainClass);
            parts.AddRange(GameArguments);
            return string.Join(" ", parts.Select(EscapeArgument));
        }

        /// <summary>给单个参数加引号（如果含空格）</summary>
        private static string EscapeArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            if (arg.Contains(' ') && !arg.StartsWith('"'))
                return "\"" + arg + "\"";
            return arg;
        }
    }

    /// <summary>
    /// 启动参数生成器接口。根据解析后的版本信息和账户信息，
    /// 生成完整的 java 启动命令。
    /// </summary>
    public interface ILaunchArgumentBuilder
    {
        /// <summary>
        /// 生成启动参数。
        /// </summary>
        /// <param name="resolved">已解析的版本信息（含合并后的 VersionInfo 和库路径列表）</param>
        /// <param name="account">玩家账户（离线 / 微软 / 外置）</param>
        /// <param name="minecraftPath">.minecraft 根目录</param>
        /// <param name="javaPath">java 可执行文件路径</param>
        /// <param name="maxMemoryMb">最大堆内存（MB）</param>
        /// <param name="minMemoryMb">初始堆内存（MB）</param>
        /// <param name="enableVersionIsolation">是否启用版本隔离。启用后 gameDir 指向 .minecraft/versions/&lt;id&gt;/，各版本独立 mods/saves</param>
        /// <param name="extraJvmArguments">额外的 JVM 参数（如 authlib-injector 注入参数），为 null 时忽略</param>
        /// <param name="windowWidth">游戏窗口宽度（&lt;=0 时不加 --width 参数，用游戏默认值）</param>
        /// <param name="windowHeight">游戏窗口高度（&lt;=0 时不加 --height 参数，用游戏默认值）</param>
        /// <param name="fullscreenOnLaunch">是否以全屏模式启动（加 --fullscreen 参数）</param>
        /// <param name="userExtraJvmArgs">用户自定义追加的 JVM 参数（空格分隔的字符串，如 "-XX:+UseG1GC"），为 null 或空时忽略</param>
        /// <returns>生成的启动参数对象</returns>
        LaunchArguments Build(
            ResolvedVersion resolved,
            AccountBase account,
            string minecraftPath,
            string javaPath,
            int maxMemoryMb,
            int minMemoryMb,
            bool enableVersionIsolation = false,
            List<string>? extraJvmArguments = null,
            int windowWidth = 0,
            int windowHeight = 0,
            bool fullscreenOnLaunch = false,
            string? userExtraJvmArgs = null);
    }
}
