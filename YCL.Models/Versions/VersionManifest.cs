using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YCL.Models.Versions
{
    /// <summary>
    /// Minecraft 官方版本清单（version_manifest.json）的根对象。
    /// 这个清单列出所有可下载的 Minecraft 版本及其版本 JSON 的 URL，
    /// 启动器用它来知道"有哪些版本可以下载"以及"每个版本的 JSON 在哪"。
    /// </summary>
    public class VersionManifest
    {
        /// <summary>最新版本信息（含正式版和快照版的 id）</summary>
        [JsonPropertyName("latest")]
        public VersionManifestLatest? Latest { get; set; }

        /// <summary>所有版本条目列表</summary>
        [JsonPropertyName("versions")]
        public List<VersionManifestEntry>? Versions { get; set; }
    }

    /// <summary>
    /// 最新版本信息。Mojang 用这个字段告诉启动器：
    /// "当前正式版是哪个 id"、"当前快照版是哪个 id"。
    /// </summary>
    public class VersionManifestLatest
    {
        /// <summary>最新正式版的 id（如 "1.20.4"）</summary>
        [JsonPropertyName("release")]
        public string? Release { get; set; }

        /// <summary>最新快照版的 id（如 "23w13a"）</summary>
        [JsonPropertyName("snapshot")]
        public string? Snapshot { get; set; }
    }

    /// <summary>
    /// 版本清单中单个版本的条目。
    /// 每个条目包含版本 id、类型、版本 JSON 的下载 URL 等信息。
    /// </summary>
    public class VersionManifestEntry
    {
        /// <summary>版本 id（如 "1.20.4"、"23w13a"）</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>
        /// 版本类型：
        /// - release：正式版
        /// - snapshot：快照版
        /// - old_beta：旧 Beta 版
        /// - old_alpha：旧 Alpha 版
        /// </summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>该版本 JSON 文件的下载 URL（走下载源转换后用于实际下载）</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        /// <summary>版本最后更新时间（ISO 8601 字符串）</summary>
        [JsonPropertyName("time")]
        public string? Time { get; set; }

        /// <summary>版本发布时间（ISO 8601 字符串）</summary>
        [JsonPropertyName("releaseTime")]
        public string? ReleaseTime { get; set; }

        /// <summary>该版本的 sha1 校验值（部分清单会带）</summary>
        [JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }

        /// <summary>版本 JSON 文件大小（字节，部分清单会带）</summary>
        [JsonPropertyName("complianceLevel")]
        public int ComplianceLevel { get; set; }
    }
}
