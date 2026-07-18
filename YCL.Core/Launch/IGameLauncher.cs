using System;
using System.Threading.Tasks;
using YCL.Models.Accounts;

namespace YCL.Core.Launch
{
    /// <summary>
    /// 启动进度信息。在游戏启动的各阶段通过事件推送给 UI。
    /// </summary>
    public class LaunchProgressEventArgs : EventArgs
    {
        /// <summary>阶段：解析版本 / 解压 natives / 启动游戏 等</summary>
        public string Stage { get; set; } = string.Empty;

        /// <summary>进度百分比（0~100，-1 表示不确定）</summary>
        public int Percent { get; set; } = -1;

        /// <summary>详细消息</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 游戏日志输出事件参数。Minecraft 进程每输出一行日志就触发一次。
    /// </summary>
    public class GameLogEventArgs : EventArgs
    {
        /// <summary>日志内容（一行）</summary>
        public string Line { get; set; } = string.Empty;

        /// <summary>是否为错误流输出（true = stderr，false = stdout）</summary>
        public bool IsError { get; set; }
    }

    /// <summary>
    /// 游戏退出事件参数。
    /// </summary>
    public class GameExitedEventArgs : EventArgs
    {
        /// <summary>进程退出码。0 = 正常退出，非 0 = 异常退出</summary>
        public int ExitCode { get; set; }

        /// <summary>是否正常退出</summary>
        public bool IsSuccess => ExitCode == 0;
    }

    /// <summary>
    /// 启动状态枚举。
    /// </summary>
    public enum LaunchState
    {
        /// <summary>空闲（未启动）</summary>
        Idle,

        /// <summary>准备中（解析版本、检查文件）</summary>
        Preparing,

        /// <summary>解压 natives</summary>
        ExtractingNatives,

        /// <summary>正在启动进程</summary>
        Starting,

        /// <summary>运行中（进程已启动且未退出）</summary>
        Running,

        /// <summary>已退出</summary>
        Exited,

        /// <summary>启动失败</summary>
        Failed
    }

    /// <summary>
    /// 游戏启动器接口。封装从版本解析到进程启动到日志捕获的完整流程。
    /// </summary>
    public interface IGameLauncher
    {
        /// <summary>当前启动状态</summary>
        LaunchState State { get; }

        /// <summary>
        /// 是否启用版本隔离。启用后启动时 gameDir 指向 .minecraft/versions/&lt;id&gt;/，
        /// 每个版本拥有独立的 mods/saves/configs 等子目录。
        /// 由调用方（如 LaunchPageViewModel）从配置读取后设置。
        /// </summary>
        bool EnableVersionIsolation { get; set; }

        /// <summary>游戏窗口宽度（&lt;=0 时用游戏默认值）。由调用方从配置读取后设置。</summary>
        int WindowWidth { get; set; }

        /// <summary>游戏窗口高度（&lt;=0 时用游戏默认值）。由调用方从配置读取后设置。</summary>
        int WindowHeight { get; set; }

        /// <summary>是否以全屏模式启动游戏。由调用方从配置读取后设置。</summary>
        bool FullscreenOnLaunch { get; set; }

        /// <summary>用户自定义追加的 JVM 参数（空格分隔字符串）。由调用方从配置读取后设置。</summary>
        string ExtraJvmArgs { get; set; }

        /// <summary>启动前是否清理旧 natives 等临时文件。由调用方从配置读取后设置。</summary>
        bool CleanBeforeLaunch { get; set; }

        /// <summary>启动进度变化事件（解析版本、解压 natives、启动游戏等阶段）</summary>
        event EventHandler<LaunchProgressEventArgs>? ProgressChanged;

        /// <summary>游戏日志输出事件（每输出一行触发一次）</summary>
        event EventHandler<GameLogEventArgs>? LogReceived;

        /// <summary>游戏进程退出事件</summary>
        event EventHandler<GameExitedEventArgs>? Exited;

        /// <summary>启动状态变化事件</summary>
        event EventHandler<LaunchState>? StateChanged;

        /// <summary>
        /// 异步启动游戏。
        /// </summary>
        /// <param name="minecraftPath">.minecraft 根目录</param>
        /// <param name="versionId">要启动的版本 id</param>
        /// <param name="account">玩家账户（离线 / 微软 / 外置）</param>
        /// <param name="javaPath">java 可执行文件路径</param>
        /// <param name="maxMemoryMb">最大堆内存（MB）</param>
        /// <param name="minMemoryMb">初始堆内存（MB）</param>
        /// <returns>启动是否成功开始（不表示游戏已退出）</returns>
        Task<bool> LaunchAsync(
            string minecraftPath,
            string versionId,
            AccountBase account,
            string javaPath,
            int maxMemoryMb,
            int minMemoryMb);

        /// <summary>强制关闭游戏进程（如果正在运行）</summary>
        void Stop();
    }
}
