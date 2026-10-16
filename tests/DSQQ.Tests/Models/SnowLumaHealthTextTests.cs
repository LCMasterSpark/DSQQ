using System.Text.Json;
using DSQQ.Models;

namespace DSQQ.Tests.Models;

// 总览页那句「SnowLuma …」的取值。
//
// 钉住的核心不变量只有一条：**永远不返回空字符串**。
// 起因是 2026-10-16 的真实故障 —— 桥接的 SnowLuma WebSocket 握手卡死、一个包都收不到，
// 而总览页那一格渲染成空白（因为桥接侧的 `connected` 是 undefined，JSON 序列化时整个键被丢掉），
// 旁边「接收健康」还是 True，看板上完全看不出异常。用户只能靠"机器人不说话"来发现问题。
public sealed class SnowLumaHealthTextTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void 桥接没返回这一项时也要说清楚()
    {
        Assert.Equal("状态未知（桥接没返回这一项）", SnowLumaHealthText.Describe(null));
    }

    [Fact]
    public void 没连上时不带时长也要给出一句话()
    {
        var status = new BridgeSnowLumaStatus { Connected = false };

        Assert.Equal("没连上", SnowLumaHealthText.Describe(status));
    }

    [Theory]
    [InlineData(45, "没连上（已经 45 秒）")]
    [InlineData(600, "没连上（已经 10 分钟）")]
    [InlineData(7380, "没连上（已经 2 小时 3 分钟）")]
    public void 没连上时把失联时长写进去(long seconds, string expected)
    {
        var status = new BridgeSnowLumaStatus { Connected = false, DisconnectedSec = seconds };

        Assert.Equal(expected, SnowLumaHealthText.Describe(status));
    }

    [Fact]
    public void 连上了但一个包都没收到_要跟没连上区分开()
    {
        var status = new BridgeSnowLumaStatus { Connected = true, LastPacketAgoMs = null };

        Assert.Equal("已连接，但一个包都还没收到", SnowLumaHealthText.Describe(status));
    }

    [Fact]
    public void 连上了就报最近一个包的类型与时间()
    {
        var status = new BridgeSnowLumaStatus
        {
            Connected = true,
            LastPacketAgoMs = 9000,
            LastPacketKind = "meta_event/heartbeat",
        };

        Assert.Equal("已连接（9 秒前收到过 meta_event/heartbeat）", SnowLumaHealthText.Describe(status));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 包类型缺失时不留下空洞(string? kind)
    {
        var status = new BridgeSnowLumaStatus { Connected = true, LastPacketAgoMs = 1000, LastPacketKind = kind };

        Assert.Equal("已连接（1 秒前收到过 未知类型）", SnowLumaHealthText.Describe(status));
    }

    // ★ 本次故障的回归测试。
    // 旧版桥接在「第一次连上之前」根本不会输出 connected 这个键（值是 undefined，
    // JSON.stringify 把它整个丢掉）。修好桥接之后这个键一定在，但客户端**不能**再依赖
    // "缺失就等于没问题" —— 缺失必须落到「没连上」这一支，而不是空白。
    [Fact]
    public void 响应里没有_connected_键时必须显示没连上而不是空白()
    {
        const string json = """
        { "accountOnline": true, "good": true, "goodSource": "get_status", "lastPacketAgoMs": null }
        """;

        var status = JsonSerializer.Deserialize<BridgeSnowLumaStatus>(json, Options);
        var text = SnowLumaHealthText.Describe(status);

        Assert.False(status!.Connected);
        Assert.Equal("没连上", text);
        Assert.NotEqual(string.Empty, text);
    }

    [Fact]
    public void 真实故障当时的响应也要被判成没连上()
    {
        // 2026-10-16 23:33 那次 /api/status 的 snowluma 段，逐字抄下来。
        // 注意 lastPacketAgoMs 是 null —— "一个包都没收到"，而旧界面显示成一片正常。
        const string json = """
        {
          "accountOnline": true,
          "good": true,
          "goodSource": "get_status",
          "goodError": null,
          "lastPacketAgoMs": null,
          "lastPacketKind": null,
          "silent": false,
          "degraded": false,
          "hint": null
        }
        """;

        var status = JsonSerializer.Deserialize<BridgeSnowLumaStatus>(json, Options);
        var text = SnowLumaHealthText.Describe(status);

        Assert.False(status!.Connected);
        Assert.Equal("没连上", text);
    }

    [Fact]
    public void 新桥接报的失联秒数应能解析出来()
    {
        const string json = """
        { "connected": false, "offline": true, "disconnectedSec": 720, "degraded": true, "hint": "已经有 12 分钟没能连上 SnowLuma 的 OneBot 服务" }
        """;

        var status = JsonSerializer.Deserialize<BridgeSnowLumaStatus>(json, Options);

        Assert.False(status!.Connected);
        Assert.True(status.Offline);
        Assert.Equal(720, status.DisconnectedSec);
        Assert.Equal("没连上（已经 12 分钟）", SnowLumaHealthText.Describe(status));
    }

    // 总览页/日志页的"是否算异常"用的就是这个判定。
    // 钉住的是：**没连上不是健康**。原先那里写的是 `connected ? Degraded : Online`，
    // 没连上反而算正常 —— 本次故障在看板上是绿的，就是这么来的。
    [Fact]
    public void 没连上不算健康()
    {
        Assert.False(SnowLumaHealthText.IsHealthy(null));
        Assert.False(SnowLumaHealthText.IsHealthy(new BridgeSnowLumaStatus()));
        Assert.False(SnowLumaHealthText.IsHealthy(new BridgeSnowLumaStatus { Connected = false, DisconnectedSec = 900 }));
        Assert.False(SnowLumaHealthText.IsHealthy(new BridgeSnowLumaStatus { Connected = true, Good = false }));
    }

    [Fact]
    public void 连着且收得到包才算健康()
    {
        Assert.True(SnowLumaHealthText.IsHealthy(new BridgeSnowLumaStatus { Connected = true, Good = true }));
    }

    [Fact]
    public void 任何输入都不能返回空白()
    {
        BridgeSnowLumaStatus?[] cases =
        [
            null,
            new BridgeSnowLumaStatus(),
            new BridgeSnowLumaStatus { Connected = true },
            new BridgeSnowLumaStatus { Connected = false, DisconnectedSec = 0 },
            new BridgeSnowLumaStatus { Connected = true, LastPacketAgoMs = 0, LastPacketKind = "x" },
        ];

        foreach (var status in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(SnowLumaHealthText.Describe(status)));
        }
    }
}
