namespace YCL.Models.Versions
{
    /// <summary>
    /// 已安装版本的信息。从 .minecraft/versions/&lt;id&gt;/&lt;id&gt;.json 中提取的关键字段，
    /// 用于在主页版本列表中展示。比 <see cref="VersionInfo"/> 轻量，只保留 UI 需要的字段。
    /// </summary>
    public class InstalledVersionInfo
    {
        /// <summary>版本 id（即 versions 目录下的子目录名，如 "1.20.4"）</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// 版本类型：release / snapshot / old_beta / old_alpha 等。
        /// 如果 JSON 里没写，默认记为 "release"。
        /// </summary>
        public string Type { get; set; } = "release";

        /// <summary>版本目录的绝对路径（.minecraft/versions/&lt;id&gt;）</summary>
        public string Directory { get; set; } = string.Empty;

        /// <summary>版本 JSON 文件的绝对路径</summary>
        public string JsonPath { get; set; } = string.Empty;

        /// <summary>是否含 inheritsFrom（即是否继承自父版本，如 Forge/Fabric 版本）</summary>
        public bool HasInheritsFrom { get; set; }

        /// <summary>父版本 id（仅当 <see cref="HasInheritsFrom"/> 为 true 时有值）</summary>
        public string? ParentVersionId { get; set; }

        /// <summary>
        /// 是否含模组加载器（Forge / Fabric / NeoForge / Quilt / LiteLoader）。
        /// 通过检查 libraries 列表中是否含特征前缀判断。
        /// </summary>
        public bool IsModded { get; set; }

        /// <summary>模组加载器名称（如 "Forge"、"Fabric"），无加载器时为 null</summary>
        public string? ModLoaderName { get; set; }

        /// <summary>
        /// 版本的友好显示名（用于 UI 展示）。
        /// 默认就是 Id，未来可扩展为读取自定义名字。
        /// </summary>
        public string DisplayName => Id;
    }
}
