using System.Text.Json;
using System.Text.Json.Serialization;
using DSQQ.Models;

namespace DSQQ.Tests.Models;

// 「桥接响应 → 模型 → 界面文本」这条链路的端到端校验。
//
// 分开测试模型与换算器是不够的：真实故障往往出在中间那一跳
//（字段名对不上、单位标错、来回换算把值吃掉）。
// 这里直接喂**桥接的真实响应**，断言最终会显示在输入框里的文本。
public sealed class ActivityLoadPipelineTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // 取自桥接 GET /api/config/activity 的实际结构。
    private const string RealResponse = """
    {
      "ok": true,
      "activity": {
        "sleepMinMs": 60000,
        "sleepMaxMs": 0,
        "recommendedSleepMinMs": 300000,
        "recommendedSleepMaxMs": 7200000,
        "maxWakePerHour": 80,
        "maxWakePerMinute": 1,
        "preSleepWaitMs": 300000,
        "batchWindowMs": 8000,
        "autoReplyCheckMs": 30000,
        "burstEnabled": true,
        "maxSendPerHour": 60,
        "maxSendPerMinute": 8,
        "burstIntervalMinMs": 1000,
        "burstIntervalMaxMs": 3000,
        "gapBaseMs": 800,
        "gapPerCharMs": 20,
        "maxMessageChars": 50
      }
    }
    """;

    private static ActivityConfig Load() =>
        JsonSerializer.Deserialize<ActivityResponse>(RealResponse, Options)!.Activity!;

    [Fact]
    public void 桥接响应应能反序列化为活动配置()
    {
        var config = Load();

        // 字段名对不上时这里会静默变成 0 —— 正是"输入框全显示 0"的成因。
        Assert.Equal(60_000, config.SleepMinMs);
        Assert.Equal(8_000, config.BatchWindowMs);
        Assert.Equal(30_000, config.AutoReplyCheckMs);
        Assert.Equal(80, config.MaxWakePerHour);
        Assert.Equal(50, config.MaxMessageChars);
        Assert.True(config.BurstEnabled);
    }

    [Theory]
    // 秒单位字段：桥接毫秒 → 界面秒
    [InlineData("sleepMinMs", "s", "60")]
    [InlineData("sleepMaxMs", "s", "0")]
    [InlineData("preSleepWaitMs", "s", "300")]
    [InlineData("batchWindowMs", "s", "8")]
    [InlineData("autoReplyCheckMs", "s", "30")]
    // 毫秒单位字段：原样
    [InlineData("burstIntervalMinMs", "ms", "1000")]
    [InlineData("gapBaseMs", "ms", "800")]
    [InlineData("gapPerCharMs", "ms", "20")]
    // 次数类：原样
    [InlineData("maxWakePerHour", "", "80")]
    [InlineData("maxWakePerMinute", "", "1")]
    [InlineData("maxMessageChars", "", "50")]
    public void 界面应显示换算后的值(string jsonKey, string unit, string expected)
    {
        var config = Load();

        // 复刻 ReadActivityValue 的分派：按 JSON 键取模型值。
        var raw = jsonKey switch
        {
            "sleepMinMs" => config.SleepMinMs.ToString(),
            "sleepMaxMs" => config.SleepMaxMs.ToString(),
            "preSleepWaitMs" => config.PreSleepWaitMs.ToString(),
            "batchWindowMs" => config.BatchWindowMs.ToString(),
            "autoReplyCheckMs" => config.AutoReplyCheckMs.ToString(),
            "burstIntervalMinMs" => config.BurstIntervalMinMs.ToString(),
            "gapBaseMs" => config.GapBaseMs.ToString(),
            "gapPerCharMs" => config.GapPerCharMs.ToString(),
            "maxWakePerHour" => config.MaxWakePerHour.ToString(),
            "maxWakePerMinute" => config.MaxWakePerMinute.ToString(),
            "maxMessageChars" => config.MaxMessageChars.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(jsonKey), jsonKey, "未知字段"),
        };

        Assert.Equal(expected, ActivityUnitConverter.ToDisplay(raw, unit));
    }

    [Fact]
    public void 载入后不做修改再保存不应产生任何改动()
    {
        // 关键回归：载入 →（用户直接点保存）必须提交 0 项，
        // 否则每点一次保存就把配置改一遍，而用户什么也没做。
        var config = Load();

        var keys = new[]
        {
            ("sleepMinMs", "s"), ("sleepMaxMs", "s"), ("preSleepWaitMs", "s"),
            ("batchWindowMs", "s"), ("autoReplyCheckMs", "s"),
            ("burstIntervalMinMs", "ms"), ("burstIntervalMaxMs", "ms"),
            ("gapBaseMs", "ms"), ("gapPerCharMs", "ms"),
            ("maxWakePerHour", ""), ("maxWakePerMinute", ""),
            ("maxSendPerHour", ""), ("maxSendPerMinute", ""), ("maxMessageChars", ""),
        };

        var changes = 0;
        foreach (var (key, unit) in keys)
        {
            var raw = Read(config, key);
            var shown = ActivityUnitConverter.ToDisplay(raw, unit);
            Assert.True(ActivityUnitConverter.TryToBridgeValue(shown, unit, out var back));
            if (back.ToString() != raw)
            {
                changes++;
            }
        }

        Assert.Equal(0, changes);
    }

    private static string Read(ActivityConfig c, string key) => key switch
    {
        "sleepMinMs" => c.SleepMinMs.ToString(),
        "sleepMaxMs" => c.SleepMaxMs.ToString(),
        "preSleepWaitMs" => c.PreSleepWaitMs.ToString(),
        "batchWindowMs" => c.BatchWindowMs.ToString(),
        "autoReplyCheckMs" => c.AutoReplyCheckMs.ToString(),
        "burstIntervalMinMs" => c.BurstIntervalMinMs.ToString(),
        "burstIntervalMaxMs" => c.BurstIntervalMaxMs.ToString(),
        "gapBaseMs" => c.GapBaseMs.ToString(),
        "gapPerCharMs" => c.GapPerCharMs.ToString(),
        "maxWakePerHour" => c.MaxWakePerHour.ToString(),
        "maxWakePerMinute" => c.MaxWakePerMinute.ToString(),
        "maxSendPerHour" => c.MaxSendPerHour.ToString(),
        "maxSendPerMinute" => c.MaxSendPerMinute.ToString(),
        "maxMessageChars" => c.MaxMessageChars.ToString(),
        _ => "0",
    };
}
