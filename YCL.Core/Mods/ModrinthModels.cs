using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YCL.Core.Mods
{
    /// <summary>
    /// Modrinth 搜索接口返回的单个结果项（hit）。
    /// 从 GET /v2/search 响应中的 hits 数组项解析。
    /// 字段对应 Modrinth API 文档：https://docs.modrinth.com/
    /// </summary>
    public class ModrinthSearchResult
    {
        /// <summary>项目 id（base62 字符串，如 "AANobbMI"）</summary>
        [JsonPropertyName("project_id")]
        public string ProjectId { get; set; } = string.Empty;

        /// <summary>项目类型（mod / modpack / resourcepack / shader / world）</summary>
        [JsonPropertyName("project_type")]
        public string ProjectType { get; set; } = string.Empty;

        /// <summary>项目显示名（人类可读）</summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>项目简介</summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>下载次数</summary>
        [JsonPropertyName("downloads")]
        public long Downloads { get; set; }

        /// <summary>关注数</summary>
        [JsonPropertyName("follows")]
        public long Follows { get; set; }

        /// <summary>图标 URL（Modrinth CDN）</summary>
        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        /// <summary>项目作者</summary>
        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        /// <summary>项目页面 URL（https://modrinth.com/mod/{slug}）</summary>
        [JsonPropertyName("slug")]
        public string Slug { get; set; } = string.Empty;

        /// <summary>支持的版本列表（含 Minecraft 版本与加载器版本混合）</summary>
        [JsonPropertyName("versions")]
        public List<string> Versions { get; set; } = new();

        /// <summary>最新版本号</summary>
        [JsonPropertyName("latest_version")]
        public string? LatestVersion { get; set; }
    }

    /// <summary>
    /// Modrinth 搜索接口的完整响应结构。
    /// 顶层包含 hits 数组、总数、限制与偏移量。
    /// </summary>
    public class ModrinthSearchResponse
    {
        [JsonPropertyName("hits")]
        public List<ModrinthSearchResult> Hits { get; set; } = new();

        [JsonPropertyName("total_hits")]
        public int TotalHits { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("offset")]
        public int Offset { get; set; }
    }

    /// <summary>
    /// Modrinth 项目版本信息。
    /// 从 GET /v2/project/{id}/version 响应解析（一个数组）。
    /// 一个项目可能有多个版本，每个版本对应一组文件与兼容性信息。
    /// </summary>
    public class ModrinthVersion
    {
        /// <summary>版本 id（base62 字符串）</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>项目 id</summary>
        [JsonPropertyName("project_id")]
        public string ProjectId { get; set; } = string.Empty;

        /// <summary>版本号（开发者自定义，如 "1.0.0"）</summary>
        [JsonPropertyName("version_number")]
        public string VersionNumber { get; set; } = string.Empty;

        /// <summary>版本名（人类可读）</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>支持的 Minecraft 版本列表</summary>
        [JsonPropertyName("game_versions")]
        public List<string> GameVersions { get; set; } = new();

        /// <summary>支持的加载器列表（fabric / forge / quilt / neoforge 等）</summary>
        [JsonPropertyName("loaders")]
        public List<string> Loaders { get; set; } = new();

        /// <summary>是否为推荐版本</summary>
        [JsonPropertyName("featured")]
        public bool Featured { get; set; }

        /// <summary>发布时间（ISO 8601 字符串）</summary>
        [JsonPropertyName("date_published")]
        public string DatePublished { get; set; } = string.Empty;

        /// <summary>下载次数</summary>
        [JsonPropertyName("downloads")]
        public long Downloads { get; set; }

        /// <summary>该版本的文件列表（一般只有一个文件）</summary>
        [JsonPropertyName("files")]
        public List<ModrinthFile> Files { get; set; } = new();

        /// <summary>版本类型（release / beta / alpha）</summary>
        [JsonPropertyName("version_type")]
        public string VersionType { get; set; } = "release";

        /// <summary>显示名（优先用 name，没有则用 version_number）</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? VersionNumber : Name;
    }

    /// <summary>
    /// Modrinth 版本中的单个文件信息。
    /// </summary>
    public class ModrinthFile
    {
        /// <summary>文件校验值（一般 sha1）</summary>
        [JsonPropertyName("hashes")]
        public ModrinthFileHashes? Hashes { get; set; }

        /// <summary>文件下载 URL（Modrinth CDN 直链，无需 Key）</summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>文件名</summary>
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        /// <summary>是否为主文件（true 表示 primary，false 表示附加文件如源码等）</summary>
        [JsonPropertyName("primary")]
        public bool Primary { get; set; }

        /// <summary>文件大小（字节）</summary>
        [JsonPropertyName("size")]
        public long Size { get; set; }

        /// <summary>文件类型（required-resource / optional-resource 等）</summary>
        [JsonPropertyName("file_type")]
        public string? FileType { get; set; }
    }

    /// <summary>Modrinth 文件校验值</summary>
    public class ModrinthFileHashes
    {
        [JsonPropertyName("sha1")]
        public string Sha1 { get; set; } = string.Empty;

        [JsonPropertyName("sha512")]
        public string Sha512 { get; set; } = string.Empty;
    }
}
