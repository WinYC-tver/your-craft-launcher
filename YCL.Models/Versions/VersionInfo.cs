using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YCL.Models.Versions
{
    /// <summary>
    /// Minecraft 版本 JSON 的根对象（对应 .minecraft/versions/&lt;id&gt;/&lt;id&gt;.json）。
    /// 包含版本所需的所有元信息：主类、启动参数、依赖库、资源索引等。
    /// </summary>
    public class VersionInfo
    {
        /// <summary>版本 id，如 "1.20.4"</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>版本类型：release / snapshot / old_beta / old_alpha 等</summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>主类全名（一般是 net.minecraft.client.main.Main）</summary>
        [JsonPropertyName("mainClass")]
        public string? MainClass { get; set; }

        /// <summary>
        /// 父版本 id。Forge / Fabric 等模组加载器的版本 JSON 会含此字段，
        /// 指向其依赖的原版 Minecraft 版本。解析时需要递归合并父版本的
        /// libraries 与 arguments。
        /// </summary>
        [JsonPropertyName("inheritsFrom")]
        public string? InheritsFrom { get; set; }

        /// <summary>
        /// 旧版本（1.12 及以下）的启动参数字符串，
        /// 形如 "${auth_player_name} ${auth_session} --version ${version_name} ..."。
        /// 新版本（1.13+）改用 arguments 字段。
        /// </summary>
        [JsonPropertyName("minecraftArguments")]
        public string? MinecraftArguments { get; set; }

        /// <summary>新版本（1.13+）的启动参数结构，含 game 与 jvm 两个数组</summary>
        [JsonPropertyName("arguments")]
        public VersionArguments? Arguments { get; set; }

        /// <summary>本版本依赖的所有库列表（用于 classpath 与 natives 解压）</summary>
        [JsonPropertyName("libraries")]
        public List<Library>? Libraries { get; set; }

        /// <summary>资源索引信息（assets 索引文件的位置与校验数据）</summary>
        [JsonPropertyName("assetIndex")]
        public AssetIndex? AssetIndex { get; set; }

        /// <summary>资源索引名（旧版本字段，1.13+ 改用 assetIndex.id）</summary>
        [JsonPropertyName("assets")]
        public string? Assets { get; set; }

        /// <summary>日志配置（一般是 log4j2 的 xml 文件）</summary>
        [JsonPropertyName("logging")]
        public LoggingConfig? Logging { get; set; }

        /// <summary>客户端 jar 文件的下载信息</summary>
        [JsonPropertyName("downloads")]
        public VersionDownloads? Downloads { get; set; }

        /// <summary>版本发布时间（ISO 8601 字符串，可选字段）</summary>
        [JsonPropertyName("releaseTime")]
        public string? ReleaseTime { get; set; }

        /// <summary>版本更新时间（ISO 8601 字符串，可选字段）</summary>
        [JsonPropertyName("time")]
        public string? Time { get; set; }

        /// <summary>最低 Java 兼容版本（如 17），不是所有版本都有</summary>
        [JsonPropertyName("javaVersion")]
        public JsonElement? JavaVersion { get; set; }
    }
}
