using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSQQ.Models;

// 把调试面板里填的一格文本变成要提交的 JSON 值。
// 每种参数类型的写法都不一样（正整数要数字、数组要 JSON 数组、raw 要按 JSON 解析），
// 猜错就会得到"HTTP 200 但 action 失败"或者更糟——成功的错误调用，所以规则集中放这里并配测试。

// 单个参数的解析结果。Ok 为真且 Value 为 null 表示"这个参数不传"
public sealed record ParamParseResult(bool Ok, JsonNode? Value, string? Error);

// 一个参数格子的输入
public sealed record DebugParamInput(DebugParam Param, string? Text);

// 整个请求体的组装结果
public sealed record DebugParamSetResult(bool Ok, JsonObject? Body, string? Error);

public static class DebugParamCodec
{
    // 解析一个参数格子。空文本 = 不传（必填的则报错）
    public static ParamParseResult Parse(DebugParam param, string? text)
    {
        ArgumentNullException.ThrowIfNull(param);

        var raw = text?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return param.Required
                ? new ParamParseResult(false, null, $"「{param.Name}」是必填的，不能空着")
                : new ParamParseResult(true, null, null);
        }

        switch (param.Type)
        {
            case "uint":
            case "int":
            case "messageId":
            {
                if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                {
                    return Fail(param, raw, "要填整数");
                }

                // 实测 messageId 的 schema 是 not const 0
                if (param.Type == "messageId" && number == 0)
                {
                    return Fail(param, raw, "不能为 0");
                }

                var (min, max) = Range(param);
                if (min is { } lo && number < lo)
                {
                    return Fail(param, raw, $"不能小于 {Format(lo)}");
                }

                if (max is { } hi && number > hi)
                {
                    return Fail(param, raw, $"不能大于 {Format(hi)}");
                }

                return new ParamParseResult(true, JsonValue.Create(number), null);
            }

            case "number":
            {
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    return Fail(param, raw, "要填数字");
                }

                var (min, max) = Range(param);
                if (min is { } lo && value < lo)
                {
                    return Fail(param, raw, $"不能小于 {Format(lo)}");
                }

                if (max is { } hi && value > hi)
                {
                    return Fail(param, raw, $"不能大于 {Format(hi)}");
                }

                return new ParamParseResult(true, JsonValue.Create(value), null);
            }

            case "bool":
            {
                if (!TryBool(raw, out var flag))
                {
                    return Fail(param, raw, "要填 true 或 false");
                }

                return new ParamParseResult(true, JsonValue.Create(flag), null);
            }

            case "enum":
            {
                var options = param.Options;
                if (options.Count > 0 && !options.Any(o => string.Equals(o, raw, StringComparison.OrdinalIgnoreCase)))
                {
                    return Fail(param, raw, "只能是：" + string.Join(" / ", options));
                }

                return new ParamParseResult(true, JsonValue.Create(raw), null);
            }

            case "string[]":
            {
                var items = SplitList(raw);
                if (items.Count == 0)
                {
                    return new ParamParseResult(true, null, null);
                }

                var array = new JsonArray();
                foreach (var item in items)
                {
                    array.Add(item);
                }

                return new ParamParseResult(true, array, null);
            }

            case "uint[]":
            {
                var items = SplitList(raw);
                if (items.Count == 0)
                {
                    return new ParamParseResult(true, null, null);
                }

                var array = new JsonArray();
                foreach (var item in items)
                {
                    if (!long.TryParse(item, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) || n < 1)
                    {
                        return Fail(param, item, "数组里只能填正整数");
                    }

                    array.Add(n);
                }

                return new ParamParseResult(true, array, null);
            }

            case "raw":
            {
                // 整个文本当作一个 JSON 值试一次；不是合法 JSON 就当字符串（路径这类就是字符串）
                return TryJson(raw, out var node)
                    ? new ParamParseResult(true, node, null)
                    : new ParamParseResult(true, JsonValue.Create(raw), null);
            }

            case "message":
            {
                // 只在看着像 JSON 时才按 JSON 解析 —— 否则一句"123"会被当成数字发出去
                if ((raw.StartsWith('[') || raw.StartsWith('{')) && TryJson(raw, out var node))
                {
                    return new ParamParseResult(true, node, null);
                }

                return new ParamParseResult(true, JsonValue.Create(raw), null);
            }

            default:
                return new ParamParseResult(true, JsonValue.Create(raw), null);
        }
    }

    // 组装整个 params 对象。不传的参数不进 body，必填没填则 Ok 为假
    public static DebugParamSetResult Build(IReadOnlyList<DebugParamInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var body = new JsonObject();
        var errors = new List<string>();

        foreach (var input in inputs)
        {
            var result = Parse(input.Param, input.Text);
            if (!result.Ok)
            {
                errors.Add(result.Error ?? $"「{input.Param.Name}」填得不对");
                continue;
            }

            if (result.Value is not null && !string.IsNullOrWhiteSpace(input.Param.Name))
            {
                body[input.Param.Name!] = result.Value;
            }
        }

        return errors.Count > 0
            ? new DebugParamSetResult(false, null, string.Join('\n', errors))
            : new DebugParamSetResult(true, body, null);
    }

    // 数组参数的分隔：逗号或换行。不切空格 —— 值本身可能带空格
    private static List<string> SplitList(string text) =>
        text.Split([',', '，', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static bool TryBool(string text, out bool value)
    {
        switch (text.ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "y":
            case "是":
                value = true;
                return true;
            case "false":
            case "0":
            case "no":
            case "n":
            case "否":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryJson(string text, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(text);
            return node is not null;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    private static (double? Min, double? Max) Range(DebugParam param)
    {
        if (param.Schema is not { } schema || schema.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (ReadNumber(schema, "minimum"), ReadNumber(schema, "maximum"));
    }

    private static double? ReadNumber(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static ParamParseResult Fail(DebugParam param, string text, string why) =>
        new(false, null, $"「{param.Name}」{why}（你填的是「{text}」）");
}
