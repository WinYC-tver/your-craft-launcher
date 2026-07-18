using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YCL.Core.Mods
{
    /// <summary>
    /// CurseForge 模组搜索结果中单个模组的信息。
    /// 从 GET /v1/mods/search 响应中的 data 数组项解析。
    /// 字段对应 CurseForge API 官方文档：https://docs.curseforge.com/
    /// </summary>
    public class CurseForgeMod
    {
        /// <summary>模组在 CurseForge 的唯一 id（数字）</summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>模组名（人类可读）</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>模组一句话简介</summary>
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        /// <summary>下载次数</summary>
        [JsonPropertyName("downloadCount")]
        public long DownloadCount { get; set; }

        /// <summary>图标 URL（CurseForge CDN）</summary>
        [JsonPropertyName("logoUrl")]
        public string LogoUrl { get; set; } = string.Empty;

        /// <summary>网页 URL（在 CurseForge 站点的页面）</summary>
        [JsonPropertyName("websiteUrl")]
        public string WebsiteUrl { get; set; } = string.Empty;

        /// <summary>最新文件列表（粗略信息，详细文件用 GetModFilesAsync 获取）</summary>
        [JsonPropertyName("latestFiles")]
        public List<CurseForgeFile> LatestFiles { get; set; } = new();

        /// <summary>类别 id 列表（用于区分 mod / resourcepack / shader 等）</summary>
        [JsonPropertyName("primaryCategoryId")]
        public int PrimaryCategoryId { get; set; }
    }

    /// <summary>
    /// CurseForge 模组文件信息。
    /// 从 GET /v1/mods/{modId}/files 响应中的 data 数组项解析。
    /// </summary>
    public class CurseForgeFile
    {
        /// <summary>文件 id（数字）</summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>显示名（一般是 "modname-1.0.0.jar"）</summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>实际文件名</summary>
        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        /// <summary>发布日期（ISO 8601 字符串）</summary>
        [JsonPropertyName("fileDate")]
        public string FileDate { get; set; } = string.Empty;

        /// <summary>下载 URL（CurseForge CDN 直链，需要带 API Key 才能访问）</summary>
        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; set; }

        /// <summary>支持的 Minecraft 版本列表（如 ["1.20.4", "1.20.3"]）</summary>
        [JsonPropertyName("gameVersions")]
        public List<string> GameVersions { get; set; } = new();

        /// <summary>文件大小（字节）</summary>
        [JsonPropertyName("fileLength")]
        public long FileLength { get; set; }

        /// <summary>文件依赖列表（含其他 mod 依赖）</summary>
        [JsonPropertyName("dependencies")]
        public List<CurseForgeDependency> Dependencies { get; set; } = new();

        /// <summary>是否为 release 版本（1）或 beta（2）/ alpha（3）</summary>
        [JsonPropertyName("releaseType")]
        public int ReleaseType { get; set; }
    }

    /// <summary>CurseForge 文件依赖项</summary>
    public class CurseForgeDependency
    {
        /// <summary>依赖的 modId</summary>
        [JsonPropertyName("modId")]
        public int ModId { get; set; }

        /// <summary>依赖类型（1=EmbeddedLibrary, 2=OptionalDependency, 3=RequiredDependency, 4=Tool, 5=Incompatible, 6=Include）</summary>
        [JsonPropertyName("relationType")]
        public int RelationType { get; set; }
    }

    /// <summary>
    /// CurseForge 搜索接口的完整响应结构。
    /// 顶层包含 data 数组与分页信息。
    /// </summary>
    public class CurseForgeSearchResponse
    {
        [JsonPropertyName("data")]
        public List<CurseForgeMod> Data { get; set; } = new();

        [JsonPropertyName("pagination")]
        public CurseForgePagination? Pagination { get; set; }
    }

    /// <summary>CurseForge 分页信息</summary>
    public class CurseForgePagination
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; }

        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }
    }
}
