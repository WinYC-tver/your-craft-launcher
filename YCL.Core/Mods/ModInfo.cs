using System.Collections.Generic;

namespace YCL.Core.Mods
{
    /// <summary>
    /// 模组信息。从 .jar / .disabled 文件中解析出的关键信息，
    /// 用于在模组管理页面的卡片列表中展示。
    ///
    /// 字段来源：
    /// - Fabric 模组：fabric.mod.json（id / name / version / description / authors / icon / depends）
    /// - Forge 1.13+：META-INF/mods.toml（mods[0].modId / DisplayName / version / description / logoFile）
    /// - Forge 1.12-：META-INF/mcmod.info（modList[0].modid / name / version / description / authorList）
    /// </summary>
    public class ModInfo
    {
        /// <summary>模组文件名（如 "sodium-fabric-0.5.3.jar"）</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>模组文件完整路径（如 "C:\...\.minecraft\mods\sodium-fabric-0.5.3.jar"）</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>模组 id（Fabric 的 modId / Forge 的 modId）</summary>
        public string ModId { get; set; } = string.Empty;

        /// <summary>模组显示名（人类可读的名字）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>模组版本（如 "0.5.3"）</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>模组描述（一句话简介）</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>作者列表（一个模组可能有多个作者）</summary>
        public List<string> Authors { get; set; } = new();

        /// <summary>
        /// 兼容的 Minecraft 版本（从 depends.minecraft 推断）。
        /// 可能为空（元数据中未声明）。
        /// </summary>
        public string MinecraftVersion { get; set; } = string.Empty;

        /// <summary>加载器类型（Fabric / Forge / Quilt / NeoForge / LiteLoader / Unknown）</summary>
        public LoaderType LoaderType { get; set; } = LoaderType.Unknown;

        /// <summary>是否启用（.jar 文件为 true，.disabled 文件为 false）</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// 图标文件路径（解析后从 jar 内提取到临时目录的本地路径）。
        /// 可能为空（模组无图标或提取失败）。
        /// </summary>
        public string? LogoPath { get; set; }

        /// <summary>
        /// 模组的友好显示名（用于 UI 展示）。
        /// 如果元数据中有 name 字段则用 name，否则回退到文件名。
        /// </summary>
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FileName : Name;

        /// <summary>作者显示文字（多个作者用逗号连接）</summary>
        public string AuthorsDisplay => Authors.Count == 0 ? "未知" : string.Join(", ", Authors);

        /// <summary>加载器显示文字</summary>
        public string LoaderTypeDisplay => LoaderType.ToString();

        public override string ToString() => $"{DisplayName} {Version} ({LoaderType})";
    }
}
