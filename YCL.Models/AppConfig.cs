using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YCL.Models
{
    /// <summary>
    /// 启动器的全局配置。所有配置项都会被序列化到 config.json 中，
    /// 启动时读取、修改后保存。用强类型对象方便各处调用。
    /// </summary>
    public class AppConfig
    {
        /// <summary>主题模式：Default=跟随系统，Light=亮色，Dark=暗色</summary>
        public ThemeMode ThemeMode { get; set; } = ThemeMode.Default;

        /// <summary>
        /// 自定义强调色，存为十六进制字符串（如 "#FF0078D7"）。
        /// 用字符串而不是 Color 类型，是因为 Color 属于 WPF（PresentationCore），
        /// 放在 YCL.Models 这个非 WPF 类库里会带来不必要的依赖。
        /// 为 null 或空时表示使用系统强调色。
        /// </summary>
        public string? AccentColorHex { get; set; }

        /// <summary>.minecraft 启动器游戏目录路径</summary>
        public string MinecraftPath { get; set; } = string.Empty;

        /// <summary>Java 运行时路径（javaw.exe 的完整路径）</summary>
        public string JavaPath { get; set; } = string.Empty;

        /// <summary>下载源：Official=官方，BMCLAPI=国内镜像，MCBBS=MCBBS 镜像</summary>
        public DownloadSource DownloadSource { get; set; } = DownloadSource.Official;

        /// <summary>下载并发数（同时下载的文件数）</summary>
        public int DownloadThreads { get; set; } = 8;

        /// <summary>下载失败时的重试次数</summary>
        public int RetryCount { get; set; } = 3;

        /// <summary>
        /// 是否启用版本隔离。启用后每个版本拥有独立的 mods/saves/configs 等目录，
        /// 通过启动参数 --gameDir 指向 .minecraft/versions/&lt;id&gt;/ 实现。
        /// 关闭时所有版本共享 .minecraft/ 下的 mods/saves 等。
        /// </summary>
        public bool EnableVersionIsolation { get; set; } = false;

        /// <summary>
        /// 主页最后选中的版本 id。用于在启动页加载时默认选中同一版本，
        /// 让主页与启动页的版本选择保持一致。为空时启动页选第一个版本。
        /// </summary>
        public string LastSelectedVersion { get; set; } = string.Empty;

        /// <summary>
        /// CurseForge API Key。CurseForge API 调用需要在请求头带 X-API-Key。
        /// 用户可在 https://console.curseforge.com 注册开发者账号获取。
        /// 为空时在线模组搜索降级到仅 Modrinth（Modrinth 公开 API 无需 Key）。
        /// </summary>
        public string CurseForgeApiKey { get; set; } = string.Empty;

        // ====== 启动设置 ======

        /// <summary>最大堆内存 -Xmx（单位 MB，默认 2048）</summary>
        public int MaxMemory { get; set; } = 2048;

        /// <summary>初始堆内存 -Xms（单位 MB，默认 512）</summary>
        public int MinMemory { get; set; } = 512;

        /// <summary>游戏窗口宽度（默认 854，Minecraft 默认值）</summary>
        public int WindowWidth { get; set; } = 854;

        /// <summary>游戏窗口高度（默认 480，Minecraft 默认值）</summary>
        public int WindowHeight { get; set; } = 480;

        /// <summary>是否以全屏模式启动游戏（--fullscreen 参数）</summary>
        public bool FullscreenOnLaunch { get; set; } = false;

        /// <summary>
        /// 启动游戏后是否自动关闭启动器窗口。
        /// 注意：实际关闭动作由启动流程在游戏进程成功启动后执行。
        /// </summary>
        public bool CloseAfterLaunch { get; set; } = false;

        /// <summary>
        /// 额外的 JVM 参数（用户自定义追加，多参数用空格分隔，
        /// 如 "-XX:+UseG1GC -XX:MaxGCPauseMillis=50"）。为空时不追加。
        /// </summary>
        public string ExtraJvmArgs { get; set; } = string.Empty;

        /// <summary>启动前是否自动清理临时文件（如旧 natives 目录）</summary>
        public bool CleanBeforeLaunch { get; set; } = false;

        // ====== 系统设置 ======

        /// <summary>是否在启动器启动时自动检查更新（默认 true）</summary>
        public bool CheckUpdateOnStartup { get; set; } = true;

        /// <summary>
        /// 启动器更新检查使用的 GitHub 仓库（格式 "owner/repo"，默认 "YCL-Team/YCL"）。
        /// 用于请求 GitHub Release API 获取最新版本。
        /// </summary>
        public string UpdateRepo { get; set; } = "YCL-Team/YCL";

        /// <summary>是否开机自启动（写入注册表 HKCU\...\Run）</summary>
        public bool LaunchOnStartup { get; set; } = false;

        // ====== 个性化设置 ======

        /// <summary>
        /// 窗口背景效果类型。
        /// Acrylic=亚克力（半透明磨砂），Mica=云母（柔光），MicaAlt=云母Alt（顶部加强），
        /// Default=不应用特殊效果（保持系统默认）。
        /// 默认 Acrylic，与启动器 v26 默认风格保持一致。
        /// </summary>
        public BackdropType Backdrop { get; set; } = BackdropType.Acrylic;

        /// <summary>
        /// 自定义壁纸图片的本地路径（png/jpg）。
        /// 为 null 时表示不使用自定义壁纸，仅显示背景效果。
        /// </summary>
        public string? WallpaperPath { get; set; }

        /// <summary>
        /// 壁纸的不透明度（0~1）。
        /// 0=完全透明，1=完全不透明。默认 0.3，保证内容可读性。
        /// </summary>
        public double WallpaperOpacity { get; set; } = 0.3;

        /// <summary>是否启用页面切换动画（淡入 + 上滑）</summary>
        public bool EnableAnimations { get; set; } = true;

        // ====== 多语言设置（v26.1.0.5）======

        /// <summary>界面语言（默认简体中文）</summary>
        public Language Language { get; set; } = Language.zhCN;

        // ====== 下载增强设置（v26.1.0.5）======

        /// <summary>
        /// 资源文件名格式（{name}=资源名称，{file}=原文件名）。
        /// 例如 "{name}-{file}" → create-1.21.1-6.0.4-我是名称（视实现而异）。
        /// </summary>
        public string ResourceFileNameFormat { get; set; } = "{name}-{file}";

        // ====== 账户 UUID 设置（v26.1.0.5）======

        /// <summary>
        /// 离线账户 UUID 生成模式。
        /// IndustryStandard=行业规范（MD5 哈希 "OfflinePlayer:" + 玩家名，与 Java 版一致），
        /// PCL=PCL 风格（SHA256 哈希玩家名取前 16 字节），
        /// Custom=自定义（用户输入）。
        /// </summary>
        public UuidMode UuidMode { get; set; } = UuidMode.IndustryStandard;

        // ====== 下载引擎设置 ======

        /// <summary>
        /// 下载文件时使用的引擎。
        /// Default=默认多线程下载器，PclCe=PCL CE 下载引擎，Ghost=Ghost Downloader 3。
        /// </summary>
        public DownloadEngine DownloadEngine { get; set; } = DownloadEngine.Default;

        // ====== 主页设置 ======

        /// <summary>
        /// 主页类型。
        /// Preset=内置预设（显示版本动态），Online=联网从 URL 拉取，Local=本地 HTML/图片文件。
        /// </summary>
        public HomepageType HomepageType { get; set; } = HomepageType.Preset;

        /// <summary>本地主页文件的完整路径（HTML 或图片），仅在 HomepageType=Local 时使用</summary>
        public string HomepageLocalPath { get; set; } = string.Empty;

        /// <summary>联网主页 URL，仅在 HomepageType=Online 时使用（如 https://www.minecraft.net/）</summary>
        public string HomepageUrl { get; set; } = string.Empty;

        // ====== 实例级 Java 覆盖 ======

        /// <summary>
        /// 实例级 Java 覆盖配置：key=版本 id，value=对应使用的 Java 路径。
        /// 为某版本指定专用 Java 后，启动该版本时会优先使用此路径而非全局 JavaPath。
        /// </summary>
        public Dictionary<string, string> InstanceJavaOverrides { get; set; } = new();
    }

    /// <summary>界面语言枚举（v26.1.0.5 多语言支持）</summary>
    public enum Language
    {
        /// <summary>简体中文</summary>
        zhCN = 0,

        /// <summary>繁体中文</summary>
        zhTW = 1,

        /// <summary>英语</summary>
        en = 2
    }

    /// <summary>离线账户 UUID 生成模式枚举（v26.1.0.5）</summary>
    public enum UuidMode
    {
        /// <summary>行业规范：MD5 哈希 "OfflinePlayer:" + 玩家名（与 Java 版离线算法一致）</summary>
        IndustryStandard = 0,

        /// <summary>PCL 风格：SHA256 哈希玩家名取前 16 字节</summary>
        PCL = 1,

        /// <summary>自定义：用户输入 UUID 字符串</summary>
        Custom = 2
    }

    /// <summary>主题模式枚举</summary>
    public enum ThemeMode
    {
        /// <summary>跟随系统</summary>
        Default = 0,

        /// <summary>亮色主题</summary>
        Light = 1,

        /// <summary>暗色主题</summary>
        Dark = 2
    }

    /// <summary>下载源枚举</summary>
    public enum DownloadSource
    {
        /// <summary>官方源</summary>
        Official = 0,

        /// <summary>BMCLAPI 镜像</summary>
        BMCLAPI = 1,

        /// <summary>MCBBS 镜像</summary>
        MCBBS = 2
    }

    /// <summary>
    /// 窗口背景效果枚举。
    /// 对应 iNKORE.UI.WPF.Modern 的 WindowBackdrop 枚举：
    /// Default→Default, Acrylic→Acrylic, Mica→Mica, MicaAlt→Tabbed(即云母Alt)。
    /// </summary>
    public enum BackdropType
    {
        /// <summary>默认（不应用特殊背景效果）</summary>
        Default = 0,

        /// <summary>亚克力（半透明磨砂效果）</summary>
        Acrylic = 1,

        /// <summary>云母（柔光效果）</summary>
        Mica = 2,

        /// <summary>云母Alt（顶部加强的云母效果）</summary>
        MicaAlt = 3
    }

    /// <summary>下载引擎枚举：选择下载文件时使用的引擎</summary>
    public enum DownloadEngine
    {
        /// <summary>默认（启动器内置多线程下载器）</summary>
        Default = 0,

        /// <summary>PCL CE 下载引擎</summary>
        PclCe = 1,

        /// <summary>Ghost Downloader 3</summary>
        Ghost = 2
    }

    /// <summary>主页类型枚举：启动器主页面显示的内容来源</summary>
    public enum HomepageType
    {
        /// <summary>预设：显示启动器内置的版本动态资讯</summary>
        Preset = 0,

        /// <summary>联网：从网络 URL 拉取主页内容</summary>
        Online = 1,

        /// <summary>本地：从本地 HTML/图片文件加载主页内容</summary>
        Local = 2
    }
}
