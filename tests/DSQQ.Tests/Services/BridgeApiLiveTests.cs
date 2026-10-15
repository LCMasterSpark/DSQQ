using System.Net.Http;
using DSQQ.Services;

namespace DSQQ.Tests.Services;

// 直接打真实桥接端点，走 DSQQ 自己的客户端。
//
// 前面的测试用固定 JSON 样本验证了模型与换算，但样本是我照抄的 ——
// 若真实响应与样本有出入（字段名、类型、多包一层），样本测试照样全绿。
// 这里用真实端点把那一层不确定性消掉。
//
// 桥接没在跑时自动跳过，不让本地环境差异把 CI 弄红。
public sealed class BridgeApiLiveTests
{
    private static readonly Uri BaseUri = new("http://127.0.0.1:3100");

    private static async Task<bool> BridgeIsUpAsync()
    {
        try
        {
            using var probe = new BridgeApiClient(BaseUri, TimeSpan.FromSeconds(3));
            return await probe.GetStatusAsync() is not null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    [Fact]
    public async Task 真实的_status_应能解析()
    {
        if (!await BridgeIsUpAsync())
        {
            return;   // 桥接未运行：跳过
        }

        using var client = new BridgeApiClient(BaseUri, TimeSpan.FromSeconds(10));
        var status = await client.GetStatusAsync();

        Assert.NotNull(status);
    }

    [Fact]
    public async Task 真实的活跃度配置应能解析且非全零()
    {
        if (!await BridgeIsUpAsync())
        {
            return;
        }

        using var client = new BridgeApiClient(BaseUri, TimeSpan.FromSeconds(10));
        var config = await client.GetActivityConfigAsync();

        Assert.NotNull(config);

        // 关键断言：桥接给出的活跃度配置不可能**所有字段都为 0**。
        // 若全为 0，说明反序列化把值都丢了（字段名或类型不匹配），
        // 界面表现就是"输入框全部显示 0"。
        var anyNonZero = config!.SleepMinMs != 0
            || config.SleepMaxMs != 0
            || config.MaxWakePerHour != 0
            || config.BatchWindowMs != 0
            || config.MaxMessageChars != 0
            || config.AutoReplyCheckMs != 0;

        Assert.True(
            anyNonZero,
            "活跃度配置全部为 0 —— 反序列化很可能没取到值。"
            + $"实测：sleepMinMs={config.SleepMinMs} batchWindowMs={config.BatchWindowMs} "
            + $"maxWakePerHour={config.MaxWakePerHour} maxMessageChars={config.MaxMessageChars} "
            + $"autoReplyCheckMs={config.AutoReplyCheckMs}");
    }

    [Fact]
    public async Task 真实的白名单应能解析()
    {
        if (!await BridgeIsUpAsync())
        {
            return;
        }

        using var client = new BridgeApiClient(BaseUri, TimeSpan.FromSeconds(10));
        var whitelist = await client.GetWhitelistAsync();

        Assert.NotNull(whitelist);
        Assert.NotNull(whitelist!.Allow);
    }

    [Fact]
    public async Task 真实的群列表应能解析()
    {
        if (!await BridgeIsUpAsync())
        {
            return;
        }

        using var client = new BridgeApiClient(BaseUri, TimeSpan.FromSeconds(10));
        var groups = await client.GetGroupsAsync();

        // 账号在线时应能拿到群；这里只断言不抛错且每项字段齐备。
        foreach (var g in groups)
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Id), "群号不应为空 —— 字段名可能不匹配");
            Assert.False(string.IsNullOrWhiteSpace(g.Name), "群名不应为空 —— 字段名可能不匹配");
        }
    }
}
