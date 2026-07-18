using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YCL.Models.Versions
{
    /// <summary>
    /// 版本依赖的一个库。对应 libraries 数组中的一项。
    /// 一般是 .jar 文件，可能带 natives（按 OS 提取本地库）。
    /// name 字段格式为 "group:artifact:version"，可据此推出文件路径。
    /// </summary>
    public class Library
    {
        /// <summary>
        /// 库名，格式 "group:artifact:version"，如 "com.mojang:minecraft:1.20.4"。
        /// 可据此推出文件相对路径：group/artifact/version/artifact-version.jar
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>下载信息（含主 artifact 与各 classifier 的 natives 文件）</summary>
        [JsonPropertyName("downloads")]
        public LibraryDownloads? Downloads { get; set; }

        /// <summary>适用规则（按 OS 过滤是否加载此库）</summary>
        [JsonPropertyName("rules")]
        public List<Rule>? Rules { get; set; }

        /// <summary>
        /// natives 映射：OS 名称 → classifier 后缀。
        /// 如 {"windows": "natives-windows"} 表示 Windows 上加载 natives-windows classifier。
        /// 没有 natives 字段的库是纯 Java 库，不需要解压。
        /// </summary>
        [JsonPropertyName("natives")]
        public Dictionary<string, string>? Natives { get; set; }

        /// <summary>
        /// 解压配置，主要是 exclude 列表（如 ["META-INF/"]），
        /// 表示解压 natives 时要排除哪些目录或文件。
        /// </summary>
        [JsonPropertyName("extract")]
        public LibraryExtract? Extract { get; set; }

        /// <summary>校验注入规则（ Forge 等用），普通版本不用</summary>
        [JsonPropertyName("checksums")]
        public List<string>? Checksums { get; set; }
    }

    /// <summary>
    /// 库的下载信息。包含主 artifact（jar 文件）与可选的 classifiers（natives 等）。
    /// </summary>
    public class LibraryDownloads
    {
        /// <summary>主 artifact（jar 文件）</summary>
        [JsonPropertyName("artifact")]
        public Artifact? Artifact { get; set; }

        /// <summary>
        /// classifier 字典。键是 classifier 名（如 "natives-windows"），
        /// 值是对应的下载信息。Natives 库通过这个字段提供按 OS 区分的本地库文件。
        /// </summary>
        [JsonPropertyName("classifiers")]
        public Dictionary<string, Artifact>? Classifiers { get; set; }
    }

    /// <summary>
    /// 一个具体文件的下载信息。可表示库 jar、natives zip、客户端 jar 等。
    /// </summary>
    public class Artifact
    {
        /// <summary>文件相对路径（相对 .minecraft/libraries/）</summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        /// <summary>SHA1 校验值</summary>
        [JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }

        /// <summary>文件大小（字节）</summary>
        [JsonPropertyName("size")]
        public int Size { get; set; }

        /// <summary>下载 URL</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    /// <summary>库解压配置（用于 natives）</summary>
    public class LibraryExtract
    {
        /// <summary>解压时要排除的路径前缀列表（如 ["META-INF/"]）</summary>
        [JsonPropertyName("exclude")]
        public List<string>? Exclude { get; set; }
    }
}
