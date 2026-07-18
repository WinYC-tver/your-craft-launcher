using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YCL.Models.Versions
{
    /// <summary>
    /// arguments.game 与 arguments.jvm 数组中的单个元素。
    /// Minecraft 版本 JSON 中，这个元素有两种形式：
    /// 1. 纯字符串：如 "--username"
    /// 2. 对象：如 {"rules": [...], "value": "-Dos.name=Windows 10"}
    ///    其中 value 可能是单个字符串，也可能是字符串数组。
    /// 这里用 <see cref="Value"/>（JsonElement）统一保存，再提供方法转换为字符串列表。
    /// </summary>
    public class ArgumentItem
    {
        /// <summary>
        /// 适用规则列表。若为 null 或空表示无条件适用。
        /// 规则用于按操作系统、位数等条件过滤参数。
        /// </summary>
        [JsonPropertyName("rules")]
        public List<Rule>? Rules { get; set; }

        /// <summary>
        /// 参数值，可能是 string、string[] 中的任一种。
        /// 用 JsonElement 保存原始 JSON，方便后续按需解析。
        /// 当本对象由纯字符串反序列化而来时，Value 就是该字符串。
        /// </summary>
        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }

        /// <summary>
        /// 把 Value 转换为字符串列表，方便拼接命令行。
        /// - 如果 Value 是字符串：返回含单个元素的列表
        /// - 如果 Value 是数组：返回数组中所有字符串
        /// </summary>
        public List<string> GetValueAsList()
        {
            var result = new List<string>();
            if (Value.ValueKind == JsonValueKind.String)
            {
                var s = Value.GetString();
                if (!string.IsNullOrEmpty(s))
                    result.Add(s);
            }
            else if (Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s))
                            result.Add(s);
                    }
                }
            }
            return result;
        }
    }

    /// <summary>
    /// 自定义 JSON 转换器：把 arguments 数组中既可能是字符串又可能是对象的元素
    /// 统一反序列化成 <see cref="ArgumentItem"/>。
    /// - 遇到字符串：包装成 Value 为该字符串的 ArgumentItem（无 Rules）
    /// - 遇到对象：手动读取 rules 和 value 属性（避免再次触发本转换器造成无限递归）
    /// </summary>
    public class ArgumentItemConverter : JsonConverter<ArgumentItem>
    {
        /// <summary>读取 JSON 并转换为 ArgumentItem</summary>
        public override ArgumentItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // 纯字符串：包装为 ArgumentItem
            if (reader.TokenType == JsonTokenType.String)
            {
                var str = reader.GetString() ?? string.Empty;
                return new ArgumentItem
                {
                    Value = JsonSerializer.SerializeToElement(str, options)
                };
            }

            // 对象：手动读取属性（不能用 JsonSerializer.Deserialize<ArgumentItem>，
            // 否则会再次触发本转换器造成无限递归）
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"无法把 {reader.TokenType} 反序列化为 ArgumentItem");
            }

            var item = new ArgumentItem();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                var propName = reader.GetString();
                reader.Read(); // 移动到属性值的起始位置

                switch (propName)
                {
                    case "rules":
                        // 用 List<Rule> 反序列化（不会触发本转换器）
                        item.Rules = JsonSerializer.Deserialize<List<Rule>>(ref reader, options);
                        break;
                    case "value":
                        {
                            // Value 可能是 string 或 array，用 JsonElement 保存当前值
                            using var doc = JsonDocument.ParseValue(ref reader);
                            item.Value = doc.RootElement.Clone();
                            break;
                        }
                    default:
                        // 跳过未知属性
                        reader.Skip();
                        break;
                }
            }

            return item;
        }

        /// <summary>把 ArgumentItem 写回 JSON</summary>
        public override void Write(Utf8JsonWriter writer, ArgumentItem value, JsonSerializerOptions options)
        {
            // 如果没有 Rules，且 Value 是字符串，写出纯字符串（与原 JSON 形式一致）
            if ((value.Rules == null || value.Rules.Count == 0) &&
                value.Value.ValueKind == JsonValueKind.String)
            {
                writer.WriteStringValue(value.Value.GetString());
                return;
            }

            // 否则正常写出对象
            writer.WriteStartObject();
            if (value.Rules != null && value.Rules.Count > 0)
            {
                writer.WritePropertyName("rules");
                JsonSerializer.Serialize(writer, value.Rules, options);
            }
            writer.WritePropertyName("value");
            value.Value.WriteTo(writer);
            writer.WriteEndObject();
        }
    }
}
