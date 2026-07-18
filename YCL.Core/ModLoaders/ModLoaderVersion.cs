namespace YCL.Core.ModLoaders
{
    /// <summary>
    /// 模组加载器版本信息。表示一个可安装的加载器版本。
    /// </summary>
    public class ModLoaderVersion
    {
        /// <summary>加载器类型</summary>
        public ModLoaderType Type { get; set; }

        /// <summary>加载器版本号（如 Fabric 的 "0.14.21"、Forge 的 "40.2.0"）</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>对应的 Minecraft 版本（如 "1.18.2"）</summary>
        public string MinecraftVersion { get; set; } = string.Empty;

        /// <summary>是否为推荐版本</summary>
        public bool Recommended { get; set; }

        /// <summary>是否为稳定版（false 表示 beta/alpha）</summary>
        public bool Stable { get; set; } = true;

        /// <summary>下载 URL（如 installer jar 的下载地址，可为空）</summary>
        public string DownloadUrl { get; set; } = string.Empty;

        /// <summary>
        /// 原始数据（JSON 字符串），用于安装时获取详细的库信息和下载地址。
        /// 各加载器实现自己解析。
        /// </summary>
        public string? RawJson { get; set; }

        /// <summary>显示名（如 "Fabric 0.14.21"）</summary>
        public string DisplayName => $"{Type} {Version}";

        public override string ToString() => DisplayName;
    }
}
