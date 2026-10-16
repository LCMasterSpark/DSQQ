using System.Text.Json;
using System.Text.Json.Nodes;
using DSQQ.Models;

namespace DSQQ.Tests.Models;

// 调试面板的两块纯逻辑：参数格子怎么变成 JSON 值（DebugParamCodec），
// 以及清单/结果怎么显示（SnowLumaDebugModels）。
//
// 参数类型的规则不是我定的，是从 GET /api/debug/actions 的 schema 里读出来的
// （实测 11 种类型、共 198 个 action）。写错了不会报错，只会让 action 用错参数
// 悄悄跑成功 —— 所以每种类型都要有测试钉住。
public class DebugPanelTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static DebugParam Param(string json) =>
        JsonSerializer.Deserialize<DebugParam>(json, Options)!;

    private static DebugAction Action(string json) =>
        JsonSerializer.Deserialize<DebugAction>(json, Options)!;

    private static DebugInvokeResult Result(string json) =>
        JsonSerializer.Deserialize<DebugInvokeResult>(json, Options)!;

    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    // ── 参数声明解析 ──

    [Fact]
    public void 整数范围从_schema_读出来写进提示()
    {
        var param = Param("""{"name":"group_id","type":"uint","required":true,"schema":{"type":"integer","minimum":1},"desc":"群号"}""");

        Assert.Equal("正整数", param.TypeText);
        Assert.Contains("不小于 1", param.Hint);
        Assert.Contains("群号", param.Hint);
        Assert.Equal("group_id *", param.Label);
    }

    [Fact]
    public void 枚举参数的可选值来自_schema()
    {
        var param = Param("""{"name":"out_format","type":"enum","schema":{"enum":["mp3","amr","wma"]}}""");

        Assert.Equal(DebugEditor.Enum, param.Editor);
        Assert.Equal(["mp3", "amr", "wma"], param.Options);
        Assert.Contains("mp3 / amr / wma", param.Hint);
    }

    [Theory]
    [InlineData("bool", DebugEditor.Bool)]
    [InlineData("enum", DebugEditor.Enum)]
    [InlineData("raw", DebugEditor.MultiLine)]
    [InlineData("message", DebugEditor.MultiLine)]
    [InlineData("string[]", DebugEditor.MultiLine)]
    [InlineData("uint[]", DebugEditor.MultiLine)]
    [InlineData("uint", DebugEditor.Text)]
    [InlineData("string", DebugEditor.Text)]
    [InlineData("messageId", DebugEditor.Text)]
    public void 每种类型用哪种编辑控件(string type, DebugEditor expected)
    {
        Assert.Equal(expected, Param($$"""{"name":"x","type":"{{type}}"}""").Editor);
    }

    [Fact]
    public void messageId_的提示说明可以是负数()
    {
        var param = Param("""{"name":"message_id","type":"messageId","required":true,"schema":{"type":"integer","not":{"const":0}}}""");

        Assert.Contains("不能等于 0", param.Hint);
        Assert.Contains("负数", param.Hint);
    }

    // ── 值的解析 ──

    [Fact]
    public void 不填的可选参数不进请求体()
    {
        var param = Param("""{"name":"fid","type":"string"}""");

        var result = DebugParamCodec.Parse(param, "   ");

        Assert.True(result.Ok);
        Assert.Null(result.Value);
    }

    [Fact]
    public void 必填参数空着要报错()
    {
        var param = Param("""{"name":"group_id","type":"uint","required":true}""");

        var result = DebugParamCodec.Parse(param, "");

        Assert.False(result.Ok);
        Assert.Contains("必填", result.Error);
    }

    [Fact]
    public void 正整数不接受零和负数()
    {
        var param = Param("""{"name":"group_id","type":"uint","schema":{"type":"integer","minimum":1}}""");

        Assert.False(DebugParamCodec.Parse(param, "0").Ok);
        Assert.False(DebugParamCodec.Parse(param, "-5").Ok);
        Assert.False(DebugParamCodec.Parse(param, "abc").Ok);
        Assert.Equal(415520021L, DebugParamCodec.Parse(param, "415520021").Value!.GetValue<long>());
    }

    [Fact]
    public void 整数上限按_schema_卡住()
    {
        // _send_group_notice.pinned 的 schema 是 minimum 0 / maximum 1
        var param = Param("""{"name":"pinned","type":"int","schema":{"type":"integer","minimum":0,"maximum":1}}""");

        Assert.True(DebugParamCodec.Parse(param, "1").Ok);
        Assert.False(DebugParamCodec.Parse(param, "2").Ok);
        Assert.Contains("不能大于 1", DebugParamCodec.Parse(param, "2").Error);
    }

    [Fact]
    public void 消息ID不能是零但可以是负数()
    {
        var param = Param("""{"name":"message_id","type":"messageId","schema":{"type":"integer","not":{"const":0}}}""");

        Assert.False(DebugParamCodec.Parse(param, "0").Ok);
        Assert.Equal(-1234L, DebugParamCodec.Parse(param, "-1234").Value!.GetValue<long>());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("是", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("否", false)]
    public void 真假参数的多种写法(string text, bool expected)
    {
        var param = Param("""{"name":"pinned","type":"bool"}""");

        var result = DebugParamCodec.Parse(param, text);

        Assert.True(result.Ok);
        Assert.Equal(expected, result.Value!.GetValue<bool>());
    }

    [Fact]
    public void 真假参数写别的要报错()
    {
        var param = Param("""{"name":"pinned","type":"bool"}""");

        Assert.False(DebugParamCodec.Parse(param, "maybe").Ok);
    }

    [Fact]
    public void 枚举只能填列出来的值()
    {
        var param = Param("""{"name":"out_format","type":"enum","schema":{"enum":["mp3","wav"]}}""");

        Assert.True(DebugParamCodec.Parse(param, "mp3").Ok);
        Assert.False(DebugParamCodec.Parse(param, "flac").Ok);
        Assert.Contains("只能是", DebugParamCodec.Parse(param, "flac").Error);
    }

    [Fact]
    public void 文本数组按逗号或换行切但不切空格()
    {
        var param = Param("""{"name":"words","type":"string[]"}""");

        var result = DebugParamCodec.Parse(param, "hello world, 你好\n第三");

        var array = result.Value!.AsArray();
        Assert.Equal(3, array.Count);
        Assert.Equal("hello world", array[0]!.GetValue<string>());
    }

    [Fact]
    public void 数字数组里出现非正整数要报错()
    {
        var param = Param("""{"name":"user_id","type":"uint[]","required":true}""");

        Assert.True(DebugParamCodec.Parse(param, "1, 2, 3").Ok);
        Assert.False(DebugParamCodec.Parse(param, "1, x").Ok);
        Assert.False(DebugParamCodec.Parse(param, "1, -2").Ok);
    }

    [Fact]
    public void 自由JSON参数按_JSON_解析失败才当字符串()
    {
        var param = Param("""{"name":"files","type":"raw","schema":{}}""");

        Assert.Equal(JsonValueKind.Object, DebugParamCodec.Parse(param, """{"file":"a.txt"}""").Value!.GetValueKind());
        Assert.Equal(JsonValueKind.Array, DebugParamCodec.Parse(param, """[1,2]""").Value!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, DebugParamCodec.Parse(param, "42").Value!.GetValueKind());

        // 路径不是合法 JSON，原样当字符串 —— 这正是这个参数最常见的用法
        Assert.Equal(@"C:\tmp\a.txt", DebugParamCodec.Parse(param, @"C:\tmp\a.txt").Value!.GetValue<string>());
    }

    [Fact]
    public void 消息参数只在看着像JSON时才当JSON()
    {
        var param = Param("""{"name":"message","type":"message"}""");

        Assert.Equal("你好", DebugParamCodec.Parse(param, "你好").Value!.GetValue<string>());
        Assert.Equal(JsonValueKind.Array, DebugParamCodec.Parse(param, """[{"type":"text","data":{"text":"hi"}}]""").Value!.GetValueKind());

        // 「123」是一句消息，不是数字 —— 当成数字发出去就完全变味了
        Assert.Equal("123", DebugParamCodec.Parse(param, "123").Value!.GetValue<string>());
    }

    // ── 组装请求体 ──

    [Fact]
    public void 组装请求体时跳过没填的并保留填了的()
    {
        var inputs = new List<DebugParamInput>
        {
            new(Param("""{"name":"group_id","type":"uint","required":true}"""), "415520021"),
            new(Param("""{"name":"fid","type":"string"}"""), ""),
            new(Param("""{"name":"pinned","type":"bool"}"""), "true"),
        };

        var result = DebugParamCodec.Build(inputs);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Body!.Count);
        Assert.False(result.Body.ContainsKey("fid"));
    }

    [Fact]
    public void 组装请求体时把所有填错的都报出来()
    {
        var inputs = new List<DebugParamInput>
        {
            new(Param("""{"name":"group_id","type":"uint","required":true}"""), ""),
            new(Param("""{"name":"pinned","type":"bool"}"""), "maybe"),
        };

        var result = DebugParamCodec.Build(inputs);

        Assert.False(result.Ok);
        Assert.Contains("group_id", result.Error);
        Assert.Contains("pinned", result.Error);
    }

    // ── 清单的显示 ──

    [Fact]
    public void 会改数据的_action_在列表里带记号()
    {
        var write = Action("""{"name":"set_group_kick_members","summary":"踢人","readOnly":false,"category":"群管理"}""");
        var read = Action("""{"name":"get_login_info","summary":"取登录号","readOnly":true,"category":"信息"}""");

        Assert.True(write.IsWrite);
        Assert.StartsWith("⚠", write.Display);
        Assert.False(read.IsWrite);
        Assert.DoesNotContain("⚠", read.Display);
        Assert.Contains("get_login_info", read.Display);
    }

    [Fact]
    public void 详情里有分类只读性和调用约束()
    {
        var action = Action("""
            {"name":"send_qzone_msg","summary":"发说说","readOnly":false,"category":"空间",
             "params":[{"name":"ugc_right","type":"uint","required":true},{"name":"content","type":"string"}],
             "invariants":["ugc_right must be one of 1, 4, 16, 64, 128"]}
            """);

        Assert.Equal(1, action.RequiredCount);
        Assert.Contains("会改数据", action.DetailText);
        Assert.Contains("参数 2 个（必填 1）", action.DetailText);
        Assert.Contains("ugc_right must be one of", action.DetailText);
    }

    [Fact]
    public void 搜索能命中别名和说明()
    {
        var action = Action("""{"name":"upload_foward_msg","aliases":["upload_forward_msg"],"summary":"上传合并转发","readOnly":true}""");

        Assert.Contains("upload_forward_msg", action.SearchText);
        Assert.Contains("合并转发", action.SearchText);
        Assert.Contains("upload_foward_msg", action.SearchText);
    }

    // ── 调用结果 ──

    [Fact]
    public void 成败看_status_字段而不是_HTTP_状态码()
    {
        var ok = Result("""{"status":"ok","retcode":0,"data":{"user_id":3551510664}}""");
        var failed = Result("""{"status":"failed","retcode":1404,"data":null,"wording":"unknown action"}""");

        Assert.True(ok.Ok);
        Assert.False(failed.Ok);
        Assert.Contains("unknown action", failed.Summary);
    }

    [Fact]
    public void 失败时没有_wording_就用_message()
    {
        var failed = Result("""{"status":"failed","message":"无效的账号"}""");

        Assert.False(failed.Ok);
        Assert.Equal("无效的账号", failed.Reason);
        Assert.Contains("无效的账号", failed.Summary);
    }

    [Fact]
    public void 失败时两个原因都没有也不能留空()
    {
        var failed = Result("""{"status":"failed","retcode":500}""");

        Assert.Equal("（服务端没给原因）", failed.Reason);
    }

    [Fact]
    public void 返回内容缩进显示()
    {
        var result = Result("""{"status":"ok","retcode":0,"data":{"user_id":1,"nickname":"O.o"}}""");

        Assert.Contains("\n", result.DataText);
        Assert.Contains("\"nickname\": \"O.o\"", result.DataText);
    }

    [Fact]
    public void 返回内容太大要截断并说明()
    {
        var big = "[" + string.Join(",", Enumerable.Range(0, 8000)) + "]";
        var result = Result($$"""{"status":"ok","retcode":0,"data":{{big}}}""");

        Assert.Contains("只显示前", result.DataText);
        Assert.True(result.DataText.Length < DebugInvokeResult.DataDisplayLimit + 200);
    }

    [Fact]
    public void 没有_data_时给出说明而不是空白()
    {
        var result = Result("""{"status":"failed","retcode":1404,"data":null}""");

        Assert.Contains("没有返回 data", result.DataText);
    }

    [Fact]
    public void 分类显示带数量()
    {
        var category = JsonSerializer.Deserialize<DebugCategory>("""{"category":"群管理","count":21}""", Options)!;

        Assert.Equal("群管理　21 个", category.Display);
    }
}
