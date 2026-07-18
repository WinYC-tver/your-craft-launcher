using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YCL.Models.Versions
{
    /// <summary>
    /// 版本 JSON 中 arguments 字段的结构，分为 game 与 jvm 两个数组。
    /// 这是 1.13+ 版本使用的格式，替代旧版的 minecraftArguments 字符串。
    /// </summary>
    public class VersionArguments
    {
        /// <summary>
        /// 游戏参数数组（传给主类的参数），如 ["--username", "${auth_player_name}", ...]。
        /// 数组元素既可能是字符串，也可能是带 rules 的对象。
        /// </summary>
        [JsonPropertyName("game")]
        public List<ArgumentItem>? Game { get; set; }

        /// <summary>
        /// JVM 参数数组（传给 java 命令的参数），如 ["-Xmx2G", "-cp", "${classpath}", ...]。
        /// 数组元素既可能是字符串，也可能是带 rules 的对象。
        /// </summary>
        [JsonPropertyName("jvm")]
        public List<ArgumentItem>? Jvm { get; set; }
    }
}
