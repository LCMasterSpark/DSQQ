namespace DSQQ.Models;

// 消耗走势的呈现逻辑。
//
// 从视图里拆出来的原因：这段是纯计算（筛选 + 汇总 + 格式化），
// 放在 BridgeConsoleView.xaml.cs 里既让那个文件更长，也没法单元测试。
public static class CostTrendBuilder
{
    // 表格里的一行。
    public sealed record Row(
        string TimeText,
        string TierText,
        string TokensText,
        string CostText,
        string TurnsText);

    // 构建结果：行 + 汇总说明。
    public sealed record Result(IReadOnlyList<Row> Rows, string Summary);

    // 由走势数据构建表格行与汇总。
    //
    // 只保留**有消耗**的桶：默认是 24 小时 / 半小时一桶 = 48 个，
    // 绝大多数是空的；全列出来会让真正花钱的时段淹没在 0 里。
    public static Result Build(TokenSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var symbol = series.Symbol ?? string.Empty;

        var rows = series.Buckets
            .Where(b => b.Tokens > 0 || b.Cost > 0 || b.Turns > 0)
            .Select(b => new Row(
                DateTimeOffset.FromUnixTimeMilliseconds(b.Time).ToLocalTime().ToString("MM-dd HH:mm"),
                b.Tier == "peak" ? "高峰" : "空闲",
                b.Tokens.ToString("N0"),
                $"{symbol}{b.Cost:0.######}",
                b.Turns.ToString()))
            .ToList();

        if (rows.Count == 0)
        {
            return new Result(rows, "最近 24 小时没有消耗记录。");
        }

        var totalCost = series.Buckets.Sum(b => b.Cost);
        var totalTokens = series.Buckets.Sum(b => b.Tokens);

        // 高峰时段单独计数：它按倍率计价，同样的 tokens 花更多钱，
        // 这是"钱花在哪"里最值得一眼看到的信息。
        var peakBuckets = series.Buckets.Count(b => b.Tier == "peak" && (b.Tokens > 0 || b.Cost > 0));

        var summary =
            $"共 {rows.Count} 个时段有消耗，合计 {totalTokens:N0} tokens / {symbol}{totalCost:0.##}"
            + $"；其中有消耗的高峰时段 {peakBuckets} 个（高峰按倍率计价）。";

        return new Result(rows, summary);
    }
}
