using System.Text.Json;
using DSQQ.Models;

namespace DSQQ.Tests.Models;

// /api/status 的 allowGroups / allowPrivate 解析。
//
// 这是一次真实故障的回归：桥接把 QQ 号**以数字**返回（"allowGroups": [415520021]），
// 而模型按 List<string> 声明，System.Text.Json 逐个元素要求 string，
// 于是整条 /api/status 解析失败。
//
// 症状很有迷惑性：DSQQ 与桥接的 TCP 连接是通的（ESTABLISHED），
// 界面却显示"服务未启动"，所有页面都读不到数据 ——
// 因为 /api/status 是所有页面共用的入口，它一失败全都失败。
public sealed class FlexibleStringListConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void 数字数组应能解析为字符串列表()
    {
        const string json = """
        { "mode": "chat", "allowGroups": [415520021], "allowPrivate": [3551510664, 1416935939] }
        """;

        var status = JsonSerializer.Deserialize<BridgeStatus>(json, Options);

        Assert.NotNull(status);
        Assert.Equal(["415520021"], status!.AllowGroups);
        Assert.Equal(["3551510664", "1416935939"], status.AllowPrivate);
    }

    [Fact]
    public void 字符串数组也应能解析()
    {
        // 桥接换个版本改成字符串数组时不能反过来挂掉。
        const string json = """
        { "allowGroups": ["415520021", "1056515304"] }
        """;

        var status = JsonSerializer.Deserialize<BridgeStatus>(json, Options);

        Assert.NotNull(status);
        Assert.Equal(["415520021", "1056515304"], status!.AllowGroups);
    }

    [Fact]
    public void 空数组与缺字段都应得到空列表()
    {
        var empty = JsonSerializer.Deserialize<BridgeStatus>("""{ "allowGroups": [] }""", Options);
        Assert.NotNull(empty);
        Assert.Empty(empty!.AllowGroups);

        // allowGroups 缺失时用属性初始值，不应抛错。
        var missing = JsonSerializer.Deserialize<BridgeStatus>("""{ "mode": "chat" }""", Options);
        Assert.NotNull(missing);
        Assert.Empty(missing!.AllowGroups);
        Assert.Empty(missing.AllowPrivate);
    }

    [Fact]
    public void 混合类型数组应被归一()
    {
        const string json = """{ "allowGroups": [415520021, "1056515304"] }""";

        var status = JsonSerializer.Deserialize<BridgeStatus>(json, Options);

        Assert.NotNull(status);
        Assert.Equal(["415520021", "1056515304"], status!.AllowGroups);
    }

    [Fact]
    public void 真实响应应能完整解析()
    {
        // 取自桥接 GET /api/status 的实际结构（含数字形式的 QQ 号）。
        const string json = """
        {
          "mode": "chat",
          "closedAgentPreset": "",
          "role": null,
          "roleMode": "active",
          "dshReady": true,
          "ownerQQ": 3551510664,
          "allowGroups": [415520021],
          "allowPrivate": [3551510664, 1416935939],
          "socialV2Paused": false,
          "snowluma": {
            "connected": true, "accountOnline": true, "good": true,
            "goodSource": "get_status", "goodError": null,
            "lastPacketAgoMs": 26954, "lastPacketKind": "meta_event/heartbeat",
            "silent": false, "degraded": false, "hint": null
          },
          "activity": ""
        }
        """;

        var status = JsonSerializer.Deserialize<BridgeStatus>(json, Options);

        Assert.NotNull(status);
        Assert.Equal("chat", status!.Mode);
        Assert.Equal(3551510664, status.OwnerQQ);
        Assert.Equal(["415520021"], status.AllowGroups);
        Assert.True(status.SnowLuma!.Connected);
        Assert.True(status.SnowLuma.Good);
    }
}