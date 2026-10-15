using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSQQ.Models;

// 宽容的字符串列表转换器：同时接受 JSON 里的字符串与数字。
//
// 存在的理由：桥接的 /api/status 把 QQ 号以**数字**返回
//（"allowGroups": [415520021]），而界面只需要把它们当标识符比较。
// 若按 List<string> 解析，System.Text.Json 会逐个元素要求 string，
// 直接抛 "The JSON value could not be converted to System.String" ——
// 而 /api/status 是所有页面共用的入口，解析一失败整个控制台就什么都读不到。
//
// 这里把两种形式都归一为字符串：
//   "123" 或 123  → "123"
// 空数组 → 空列表；元素为 null 时跳过。
public sealed class FlexibleStringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<string>();

        if (reader.TokenType == JsonTokenType.Null)
        {
            return list;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            // 单值也接受，避免因后端偶发改成单元素而整页读不到。
            list.Add(ReadElement(ref reader));
            return list;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var value = ReadElement(ref reader);
            if (value.Length > 0)
            {
                list.Add(value);
            }
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }

    // 把一个元素读成字符串。
    // 数字走 GetDecimal：对 QQ 号这类整数是无损的（int64 范围内的整数值都精确），
    // 同时避免 GetInt64 在遇到小数时抛错。
    private static string ReadElement(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString() ?? string.Empty,
        JsonTokenType.Number => reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        _ => string.Empty,
    };
}
