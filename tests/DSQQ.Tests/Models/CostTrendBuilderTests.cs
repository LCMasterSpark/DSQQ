using DSQQ.Models;

namespace DSQQ.Tests.Models;

// 消耗走势的构建逻辑（从视图里拆出来的那段）。
public sealed class CostTrendBuilderTests
{
    private static TokenSeries Series(params TokenBucket[] buckets) => new()
    {
        Symbol = "¥",
        BucketMs = 1_800_000,
        Buckets = [.. buckets],
    };

    private static TokenBucket Bucket(long time, double cost, long tokens, int turns, string tier = "off")
        => new() { Time = time, Cost = cost, Tokens = tokens, Turns = turns, Tier = tier };

    [Fact]
    public void 只保留有消耗的时段()
    {
        var series = Series(
            Bucket(1_700_000_000_000, 0, 0, 0),
            Bucket(1_700_001_800_000, 0.5, 1000, 2),
            Bucket(1_700_003_600_000, 0, 0, 0));

        var result = CostTrendBuilder.Build(series);

        // 24 小时 48 个桶里绝大多数是空的；全列出来会让真正花钱的时段被 0 淹没。
        Assert.Single(result.Rows);
        // tokens 用 N0 格式化，带千位分隔符。
        Assert.Contains("1,000", result.Rows[0].TokensText, StringComparison.Ordinal);
    }

    [Fact]
    public void 汇总应含合计与高峰计数()
    {
        var series = Series(
            Bucket(1_700_000_000_000, 1.5, 2000, 3, tier: "peak"),
            Bucket(1_700_001_800_000, 0.5, 1000, 1));

        var result = CostTrendBuilder.Build(series);

        Assert.Contains("3,000", result.Summary, StringComparison.Ordinal);
        Assert.Contains("¥2", result.Summary, StringComparison.Ordinal);
        Assert.Contains("高峰时段 1 个", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 全空时应给出明确说明而不是空表()
    {
        var result = CostTrendBuilder.Build(Series(Bucket(1_700_000_000_000, 0, 0, 0)));

        Assert.Empty(result.Rows);
        Assert.Contains("没有消耗", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 档位应显示为中文()
    {
        var series = Series(
            Bucket(1_700_000_000_000, 1, 100, 1, tier: "peak"),
            Bucket(1_700_001_800_000, 1, 100, 1, tier: "off"));

        var result = CostTrendBuilder.Build(series);

        Assert.Equal("高峰", result.Rows[0].TierText);
        Assert.Equal("空闲", result.Rows[1].TierText);
    }

    [Fact]
    public void 金额应随桥接下发的符号()
    {
        var series = new TokenSeries
        {
            Symbol = "$",
            Buckets = [Bucket(1_700_000_000_000, 0.25, 100, 1)],
        };

        var result = CostTrendBuilder.Build(series);

        // 货币符号来自桥接，不能写死成 ¥。
        Assert.Contains("$0.25", result.Rows[0].CostText, StringComparison.Ordinal);
    }

    [Fact]
    public void 空桶列表不应抛错()
    {
        var result = CostTrendBuilder.Build(Series());

        Assert.Empty(result.Rows);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }
}
