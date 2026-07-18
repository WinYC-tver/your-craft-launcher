using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YCL.Core.Resources
{
    /// <summary>
    /// 整合包安装进度信息。整合包安装是耗时操作，需要进度反馈。
    /// 包含当前阶段、正在处理的文件、整体进度等。
    /// </summary>
    public class ModpackInstallProgress
    {
        /// <summary>当前安装阶段</summary>
        public ModpackInstallPhase Phase { get; set; }

        /// <summary>当前正在处理的文件或操作描述</summary>
        public string CurrentFile { get; set; } = string.Empty;

        /// <summary>已完成的文件数</summary>
        public int CompletedFiles { get; set; }

        /// <summary>总文件数</summary>
        public int TotalFiles { get; set; }

        /// <summary>整体进度百分比（0~100，未知时为 -1）</summary>
        public double Percent => TotalFiles > 0 ? (double)CompletedFiles / TotalFiles * 100.0 : -1;

        /// <summary>用户可读的阶段名称</summary>
        public string PhaseText => Phase switch
        {
            ModpackInstallPhase.Extracting => "解压整合包",
            ModpackInstallPhase.ParsingManifest => "解析清单文件",
            ModpackInstallPhase.InstallingMinecraft => "安装 Minecraft 版本",
            ModpackInstallPhase.InstallingLoader => "安装模组加载器",
            ModpackInstallPhase.DownloadingMods => "下载模组文件",
            ModpackInstallPhase.ApplyingOverrides => "应用整合包覆盖文件",
            ModpackInstallPhase.Completed => "安装完成",
            _ => Phase.ToString()
        };
    }

    /// <summary>整合包安装阶段</summary>
    public enum ModpackInstallPhase
    {
        /// <summary>解压整合包 zip</summary>
        Extracting,

        /// <summary>解析 manifest.json / modrinth.index.json</summary>
        ParsingManifest,

        /// <summary>安装 Minecraft 版本</summary>
        InstallingMinecraft,

        /// <summary>安装模组加载器（forge/fabric 等）</summary>
        InstallingLoader,

        /// <summary>下载所有模组文件到 mods/</summary>
        DownloadingMods,

        /// <summary>把 overrides/ 文件夹覆盖到游戏目录</summary>
        ApplyingOverrides,

        /// <summary>安装完成</summary>
        Completed
    }

    /// <summary>
    /// 整合包清单信息（从 manifest.json 或 modrinth.index.json 解析）。
    /// 把两种格式归一化为同一结构。
    /// </summary>
    public class ModpackManifest
    {
        /// <summary>整合包来源（CurseForge / Modrinth）</summary>
        public ModpackSource Source { get; set; }

        /// <summary>整合包名</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>整合包版本</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>整合包作者</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>Minecraft 版本</summary>
        public string MinecraftVersion { get; set; } = string.Empty;

        /// <summary>模组加载器（forge / fabric / quilt / neoforge）</summary>
        public string LoaderType { get; set; } = string.Empty;

        /// <summary>加载器版本</summary>
        public string LoaderVersion { get; set; } = string.Empty;

        /// <summary>模组文件列表（含来源平台信息）</summary>
        public List<ModpackFileEntry> Files { get; set; } = new();

        /// <summary>覆盖文件夹名（CurseForge 默认 "overrides"，Modrinth 默认 "overrides"）</summary>
        public string OverridesFolder { get; set; } = "overrides";
    }

    /// <summary>整合包中单个模组文件条目</summary>
    public class ModpackFileEntry
    {
        /// <summary>来源平台</summary>
        public ModpackSource Source { get; set; }

        /// <summary>项目 id（CurseForge 是数字，Modrinth 是 base62）</summary>
        public string ProjectId { get; set; } = string.Empty;

        /// <summary>文件 id（CurseForge 是数字，Modrinth 是 base62）</summary>
        public string FileId { get; set; } = string.Empty;

        /// <summary>文件名</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>下载 URL（Modrinth 直接提供，CurseForge 需要查 API）</summary>
        public string? DownloadUrl { get; set; }

        /// <summary>SHA1 校验值（Modrinth 提供，用于校验下载完整性）</summary>
        public string? Sha1 { get; set; }

        /// <summary>文件大小（字节，Modrinth 提供）</summary>
        public long Size { get; set; }
    }

    /// <summary>整合包来源</summary>
    public enum ModpackSource
    {
        /// <summary>CurseForge 整合包（manifest.json）</summary>
        CurseForge = 0,

        /// <summary>Modrinth 整合包（modrinth.index.json）</summary>
        Modrinth = 1,

        /// <summary>未知格式</summary>
        Unknown = 2
    }
}
