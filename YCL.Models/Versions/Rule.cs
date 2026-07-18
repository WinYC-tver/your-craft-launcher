using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YCL.Models.Versions
{
    /// <summary>
    /// 库或参数的适用规则。Minecraft 用 rules 来按操作系统、位数等条件
    /// 判断某个库是否需要加载、某个参数是否需要添加。
    /// 例如 natives 库通常带 rules 限定只在某个 OS 上加载。
    /// </summary>
    public class Rule
    {
        /// <summary>
        /// 动作类型：allow（允许）/ disallow（不允许）。
        /// 评估方式：
        /// - 如果没有 os 字段，allow 表示无条件允许
        /// - 如果有 os 字段，allow + os 匹配 → 允许；disallow + os 匹配 → 不允许
        /// </summary>
        [JsonPropertyName("action")]
        public string? Action { get; set; }

        /// <summary>OS 限定条件（可选）。不写表示不限定 OS。</summary>
        [JsonPropertyName("os")]
        public RuleOs? Os { get; set; }

        /// <summary>特性限定条件（可选，1.13+ 用来按特性开关过滤）</summary>
        [JsonPropertyName("features")]
        public Dictionary<string, bool>? Features { get; set; }
    }

    /// <summary>
    /// 规则中的操作系统限定条件。
    /// Minecraft 用 name 判断系统（windows / linux / osx），
    /// 用 version（正则）和 arch（x86 / x64）做更细的过滤。
    /// </summary>
    public class RuleOs
    {
        /// <summary>OS 名称：windows / linux / osx</summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>系统架构：x86 / x64</summary>
        [JsonPropertyName("arch")]
        public string? Arch { get; set; }

        /// <summary>OS 版本正则表达式（如 "^10\." 用来匹配 Windows 10）</summary>
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}
