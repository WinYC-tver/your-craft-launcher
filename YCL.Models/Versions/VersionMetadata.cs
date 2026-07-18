using System.Text.Json.Serialization;

namespace YCL.Models.Versions
{
    /// <summary>
    /// 资源索引信息。Minecraft 把贴图、音效等资源按索引文件组织，
    /// 索引文件位于 .minecraft/assets/indexes/&lt;id&gt;.json。
    /// </summary>
    public class AssetIndex
    {
        /// <summary>索引 id（如 "17" 或 "1.20"）</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>SHA1 校验值</summary>
        [JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }

        /// <summary>索引文件大小（字节）</summary>
        [JsonPropertyName("size")]
        public int Size { get; set; }

        /// <summary>所有资源文件总大小（字节）</summary>
        [JsonPropertyName("totalSize")]
        public int TotalSize { get; set; }

        /// <summary>下载 URL</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    /// <summary>日志配置（顶层，含 client 字段）</summary>
    public class LoggingConfig
    {
        /// <summary>客户端日志配置</summary>
        [JsonPropertyName("client")]
        public LoggingClient? Client { get; set; }
    }

    /// <summary>
    /// 客户端日志配置。一般是 log4j2 的 xml 配置文件，
    /// 通过 -Dlog4j.configurationFile=... 参数传给 JVM。
    /// </summary>
    public class LoggingClient
    {
        /// <summary>日志配置文件信息</summary>
        [JsonPropertyName("file")]
        public LoggingFile? File { get; set; }

        /// <summary>JVM 参数模板（含 ${path} 占位符）</summary>
        [JsonPropertyName("argument")]
        public string? Argument { get; set; }

        /// <summary>日志类型，一般是 "log4j2-xml"</summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    /// <summary>日志配置文件信息</summary>
    public class LoggingFile
    {
        /// <summary>文件 id（如 "client-1.12.xml"）</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

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

    /// <summary>版本下载信息（含客户端 jar 的下载信息）</summary>
    public class VersionDownloads
    {
        /// <summary>客户端 jar 文件下载信息</summary>
        [JsonPropertyName("client")]
        public Artifact? Client { get; set; }

        /// <summary>服务端 jar 文件下载信息（启动器一般用不到）</summary>
        [JsonPropertyName("server")]
        public Artifact? Server { get; set; }
    }
}
