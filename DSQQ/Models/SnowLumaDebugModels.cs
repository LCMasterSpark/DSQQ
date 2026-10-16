using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSQQ.Models;

// SnowLuma 调试面板：直接调 OneBot action。
// 清单：GET /api/debug/actions → { actions, categories }，实测 198 个 action、12 个分类。
// 调用：POST /api/debug/invoke，请求体 { uin, action, params }。
// 结果：{ status: "ok" | "failed", retcode, data, wording? }。
// 关键：调用失败时 HTTP 仍然是 200（未知 action 也是 200），成败只能看 status 字段。

// 调试面板里一行参数该用哪种编辑控件
public enum DebugEditor
{
    Text,
    Bool,
    Enum,
    MultiLine,
}

// 一个分类及其 action 数量
public sealed class DebugCategory
{
    [JsonPropertyName("category")] public string? Category { get; set; }

    [JsonPropertyName("count")] public int Count { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Category)
        ? $"（未分类）　{Count} 个"
        : $"{Category}　{Count} 个";
}

// 清单顶层
public sealed class DebugActionList
{
    [JsonPropertyName("actions")] public List<DebugAction>? Actions { get; set; }

    [JsonPropertyName("categories")] public List<DebugCategory>? Categories { get; set; }
}

// 一个 action 的参数声明
public sealed class DebugParam
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("required")] public bool Required { get; set; }

    [JsonPropertyName("role")] public string? Role { get; set; }

    [JsonPropertyName("desc")] public string? Desc { get; set; }

    [JsonPropertyName("schema")] public JsonElement? Schema { get; set; }

    // 界面上标签：必填的带星号
    public string Label => Required ? $"{Name} *" : Name ?? string.Empty;

    // enum 参数的可选值；不是 enum 时为空
    public IReadOnlyList<string> Options
    {
        get
        {
            if (Schema is not { } schema || schema.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            if (!schema.TryGetProperty("enum", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<string>();
            foreach (var item in values.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text);
                }
            }

            return list;
        }
    }

    // 用哪种编辑控件。bool 要能表达"不传"，所以是下拉而不是勾选框
    public DebugEditor Editor => Type switch
    {
        "bool" => DebugEditor.Bool,
        "enum" => DebugEditor.Enum,
        "raw" or "message" or "string[]" or "uint[]" => DebugEditor.MultiLine,
        _ => DebugEditor.Text,
    };

    // 类型的中文说法
    public string TypeText => Type switch
    {
        "uint" => "正整数",
        "int" => "整数",
        "number" => "数字",
        "bool" => "真假",
        "string" => "文本",
        "messageId" => "消息 ID",
        "message" => "消息内容",
        "raw" => "自由 JSON",
        "string[]" => "文本数组",
        "uint[]" => "数字数组",
        "enum" => "枚举",
        null or "" => "未知",
        _ => Type,
    };

    // 把 schema 里对填写有影响的信息说清楚，避免用户靠猜
    public string Hint
    {
        get
        {
            var parts = new List<string> { TypeText };

            if (Schema is { } schema && schema.ValueKind == JsonValueKind.Object)
            {
                var min = ReadNumber(schema, "minimum");
                var max = ReadNumber(schema, "maximum");
                if (min is not null && max is not null)
                {
                    parts.Add($"范围 {min} ~ {max}");
                }
                else if (min is not null)
                {
                    parts.Add($"不小于 {min}");
                }
                else if (max is not null)
                {
                    parts.Add($"不大于 {max}");
                }

                if (schema.TryGetProperty("not", out var not)
                    && not.ValueKind == JsonValueKind.Object
                    && not.TryGetProperty("const", out var constant))
                {
                    parts.Add($"不能等于 {constant}");
                }
            }

            switch (Type)
            {
                case "enum" when Options.Count > 0:
                    parts.Add("可选：" + string.Join(" / ", Options));
                    break;
                case "bool":
                    parts.Add("不选表示不传这个参数");
                    break;
                case "string[]":
                case "uint[]":
                    parts.Add("多个值用逗号或换行分隔");
                    break;
                case "raw":
                    parts.Add("对象、数组或字符串都行，会按 JSON 解析");
                    break;
                case "message":
                    parts.Add("纯文本直接写；要发消息段就写 JSON 数组");
                    break;
                case "messageId":
                    parts.Add("可以填负数群里看到的那个编号");
                    break;
            }

            if (!string.IsNullOrWhiteSpace(Desc))
            {
                parts.Add(Desc.Trim());
            }

            return string.Join("　·　", parts);
        }
    }

    private static string? ReadNumber(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble().ToString("0.####", CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
    }
}

// 一个可调用的 action
public sealed class DebugAction
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("aliases")] public List<string>? Aliases { get; set; }

    [JsonPropertyName("summary")] public string? Summary { get; set; }

    [JsonPropertyName("readOnly")] public bool ReadOnly { get; set; }

    [JsonPropertyName("category")] public string? Category { get; set; }

    [JsonPropertyName("params")] public List<DebugParam>? Params { get; set; }

    [JsonPropertyName("invariants")] public List<string>? Invariants { get; set; }

    // readOnly 为假就是会改数据（实测 118 个会改、80 个只读）
    public bool IsWrite => !ReadOnly;

    public IReadOnlyList<DebugParam> ParamList => Params ?? [];

    public int RequiredCount => ParamList.Count(p => p.Required);

    // 列表里那一行。会改数据的带个记号，避免在 198 条里误点
    public string Display
    {
        get
        {
            var text = IsWrite ? $"⚠ {Name}" : Name ?? string.Empty;
            return string.IsNullOrWhiteSpace(Summary) ? text : $"{text}　{Summary}";
        }
    }

    // 过滤用：名称、别名、说明、分类都能搜到
    public string SearchText => string.Join(
        '\n',
        new[] { Name, Summary, Category }.Concat(Aliases ?? []).Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();

    // 右侧详情抬头
    public string DetailText
    {
        get
        {
            var lines = new List<string>
            {
                Name ?? string.Empty,
                $"分类：{Category ?? "（未分类）"}　·　{(IsWrite ? "会改数据" : "只读")}　·　参数 {ParamList.Count} 个（必填 {RequiredCount}）",
            };

            if (!string.IsNullOrWhiteSpace(Summary))
            {
                lines.Add(Summary.Trim());
            }

            if (Aliases is { Count: > 0 })
            {
                lines.Add("别名：" + string.Join(" / ", Aliases));
            }

            if (Invariants is { Count: > 0 })
            {
                lines.Add("调用约束：");
                foreach (var item in Invariants)
                {
                    lines.Add("　· " + item);
                }
            }

            return string.Join('\n', lines);
        }
    }
}

// POST /api/debug/invoke 的结果
public sealed class DebugInvokeResult
{
    [JsonPropertyName("status")] public string? Status { get; set; }

    [JsonPropertyName("retcode")] public long? RetCode { get; set; }

    [JsonPropertyName("data")] public JsonElement? Data { get; set; }

    // 失败时服务端给的原因，两个字段都可能出现
    [JsonPropertyName("wording")] public string? Wording { get; set; }

    [JsonPropertyName("message")] public string? Message { get; set; }

    // 成败看 status，不看 HTTP 状态码
    public bool Ok => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);

    public string Reason
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Wording))
            {
                return Wording.Trim();
            }

            return string.IsNullOrWhiteSpace(Message) ? "（服务端没给原因）" : Message.Trim();
        }
    }

    public string Summary => Ok
        ? $"✓ 调用成功　retcode {RetCode?.ToString(CultureInfo.InvariantCulture) ?? "—"}"
        : $"✗ 调用失败　retcode {RetCode?.ToString(CultureInfo.InvariantCulture) ?? "—"}　{Reason}";

    // data 的缩进 JSON。太长就截断 —— 好友列表这类一次能有几十 KB，直接塞进 TextBlock 会卡
    public string DataText
    {
        get
        {
            if (Data is not { } data || data.ValueKind == JsonValueKind.Undefined)
            {
                return "（这次调用没有返回 data）";
            }

            string text;
            try
            {
                text = JsonSerializer.Serialize(data, PrettyOptions);
            }
            catch (Exception ex)
            {
                return "（无法显示返回内容：" + ex.Message + "）";
            }

            if (text.Length <= DataDisplayLimit)
            {
                return text;
            }

            return text[..DataDisplayLimit]
                + $"\n… （共 {text.Length} 字符，只显示前 {DataDisplayLimit} 字符）";
        }
    }

    public const int DataDisplayLimit = 20000;

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };
}
