using DSQQ.Models;

namespace DSQQ.Tests.Models;

// 活跃度字段的秒 ↔ 毫秒换算。
//
// 这层换算存在的理由：桥接端点收毫秒，而界面用秒（填 300 而不是 300000）。
// 它是"看起来简单、错了却很难发现"的典型 ——
// 少乘一次 1000，用户设的"5 分钟"会变成 5 秒，而且界面不会报任何错。
public sealed class ActivityUnitConverterTests
{
    [Theory]
    [InlineData("300000", "300")]
    [InlineData("1000", "1")]
    [InlineData("0", "0")]
    [InlineData("60000", "60")]
    public void 毫秒转秒_整秒时不应带小数(string milliseconds, string expected)
    {
        Assert.Equal(expected, ActivityUnitConverter.ToDisplay(milliseconds, "s"));
    }

    [Theory]
    [InlineData("1500", "1.5")]
    [InlineData("250", "0.25")]
    public void 毫秒转秒_非整秒时保留精度(string milliseconds, string expected)
    {
        // 早期实现若用整除，1500 毫秒会显示成 1 秒 —— 用户再保存一次就把 1.5 秒吃掉了。
        Assert.Equal(expected, ActivityUnitConverter.ToDisplay(milliseconds, "s"));
    }

    [Theory]
    [InlineData("300", "s", 300_000)]
    [InlineData("1", "s", 1_000)]
    [InlineData("1.5", "s", 1_500)]
    [InlineData("0", "s", 0)]
    [InlineData("500", "", 500)]
    [InlineData("25000", "ms", 25_000)]
    public void 输入转桥接值(string text, string unit, int expected)
    {
        Assert.True(ActivityUnitConverter.TryToBridgeValue(text, unit, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void 往返换算应保持原值()
    {
        // 载入 → 直接保存（不改任何东西）必须回写同样的值，
        // 否则用户只是点一下保存就把配置改了。
        int[] milliseconds = [0, 250, 1000, 1500, 60_000, 300_000, 86_400_000];

        foreach (var original in milliseconds)
        {
            var shown = ActivityUnitConverter.ToDisplay(original.ToString(), "s");
            Assert.True(ActivityUnitConverter.TryToBridgeValue(shown, "s", out var back));
            Assert.Equal(original, back);
        }
    }

    [Theory]
    [InlineData("", "s")]
    [InlineData("abc", "s")]
    [InlineData("12秒", "s")]
    [InlineData("1,5", "s")]
    public void 非法输入应被拒绝(string text, string unit)
    {
        Assert.False(ActivityUnitConverter.TryToBridgeValue(text, unit, out _));
    }

    [Fact]
    public void 超出int范围应判为非法而不是溢出()
    {
        // 秒类字段最大 86400 秒；给一个远超 int 的输入必须被拒绝，
        // 而不是相减成负数后误存进配置。
        Assert.False(ActivityUnitConverter.TryToBridgeValue("99999999", "s", out _));
        Assert.False(ActivityUnitConverter.TryToBridgeValue("999999999999", "ms", out _));
    }

    [Fact]
    public void 非秒单位字段不做换算()
    {
        // 次数类（maxWakePerHour 等）与毫秒类必须原样传递。
        Assert.Equal("12", ActivityUnitConverter.ToDisplay("12", ""));
        Assert.Equal("800", ActivityUnitConverter.ToDisplay("800", "ms"));
    }

    [Theory]
    [InlineData("s", "秒")]
    [InlineData("ms", "毫秒")]
    [InlineData("", "次")]
    public void 单位标签(string unit, string expected)
    {
        Assert.Equal(expected, ActivityUnitConverter.UnitLabel(unit));
    }

    [Fact]
    public void 预设里的秒单位字段换算后应是整数秒()
    {
        // 只有这几个字段在界面上按「秒」显示 —— 连发间隔类是毫秒，不参与换算，
        // 按"键名以 Ms 结尾"来筛会把 burstIntervalMinMs(500) 也算进来，
        // 那本来就该是 0.5 秒，不是脏数据。
        string[] secondFields =
        [
            "sleepMinMs", "sleepMaxMs", "preSleepWaitMs", "batchWindowMs", "autoReplyCheckMs",
        ];

        foreach (var preset in ActivityPresets.All)
        {
            foreach (var key in secondFields)
            {
                Assert.True(
                    preset.Values.ContainsKey(key),
                    $"预设「{preset.Name}」缺少秒单位字段 {key}");

                var shown = ActivityUnitConverter.ToDisplay(preset.Values[key].ToString(), "s");
                Assert.DoesNotContain(".", shown, StringComparison.Ordinal);
            }
        }
    }
}
