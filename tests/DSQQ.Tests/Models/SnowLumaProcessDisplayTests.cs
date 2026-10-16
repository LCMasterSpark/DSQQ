using System.Text.Json;
using DSQQ.Models;

namespace DSQQ.Tests.Models;

// QQ 账号页进程表的「QQ 号」一列。
//
// 这一列有三种来源，必须分得开：
//   1. 进程自己报的 uin（已加载的行）；
//   2. 问 QQ 的本地登录端口探出来的（**没加载的行也有**）；
//   3. 都没问到。
//
// 第 3 种还要再分：… 是"还没探"，— 是"问过了，这个进程没登录 QQ"。
// 早先只有 — 一种，未加载的行永远显示 —，看起来像"这个进程什么都没有"，
// 而实际上它可能正登录着一个号。
public sealed class SnowLumaProcessDisplayTests
{
    [Fact]
    public void 进程自己报了_uin_时应直接显示()
    {
        var process = new SnowLumaProcess { Pid = 11932, Uin = "3551510664" };

        Assert.Equal("3551510664", process.Account);
    }

    [Fact]
    public void 自己报的_uin_应优先于探测结果()
    {
        var process = new SnowLumaProcess
        {
            Pid = 11932,
            Uin = "3551510664",
            ProbedUin = "1416935939",
            ProbeAttempted = true,
        };

        Assert.Equal("3551510664", process.Account);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void uin_为空或_0_时应回退到探测结果(string? uin)
    {
        // 未注入的进程 uin 恒为字符串 "0" —— 但探测照样能问出登录的是谁。
        var process = new SnowLumaProcess
        {
            Pid = 524,
            Uin = uin,
            ProbedUin = "1416935939",
            ProbeAttempted = true,
        };

        Assert.Equal("1416935939", process.Account);
    }

    [Fact]
    public void 还没探过时应显示省略号()
    {
        var process = new SnowLumaProcess { Pid = 524, Uin = "0" };

        Assert.Equal("…", process.Account);
    }

    [Fact]
    public void 探过但没探到时应显示破折号()
    {
        var process = new SnowLumaProcess { Pid = 31388, Uin = "0", ProbeAttempted = true };

        Assert.Equal("—", process.Account);
    }

    [Fact]
    public void 探到空号时也应显示破折号()
    {
        // info 存在但 uin 是 "0"：视图会把它归一成 null，标记成探过。
        var process = new SnowLumaProcess { Pid = 31388, Uin = "0", ProbeAttempted = true, ProbedUin = null };

        Assert.Equal("—", process.Account);
    }

    [Fact]
    public void 探测字段不应参与反序列化()
    {
        // 它们是界面填的，接口不会回传。带上 JsonIgnore 才不会在
        // 将来的接口里出现同名字段时把两个来源搅在一起。
        var process = JsonSerializer.Deserialize<SnowLumaProcess>(
            """{"pid":1,"uin":"0","probedUin":"999","probeAttempted":true}""");

        Assert.NotNull(process);
        Assert.Null(process!.ProbedUin);
        Assert.False(process.ProbeAttempted);
        Assert.Equal("…", process.Account);
    }

    [Fact]
    public void 探测响应的字段名应解析正确()
    {
        // ⚠️ 是 nickName（大写 N），不是 nickname —— 写错会静默拿到 null。
        var info = JsonSerializer.Deserialize<SnowLumaProbeLoginInfo>(
            """{"port":4303,"uin":"1416935939","nickName":"秋霜玉","identityKnown":true}""");

        Assert.NotNull(info);
        Assert.Equal(4303, info!.Port);
        Assert.Equal("1416935939", info.Uin);
        Assert.Equal("秋霜玉", info.NickName);
        Assert.True(info.IdentityKnown);
    }

    [Fact]
    public void 探不到时的外壳应解出_null_而不是抛异常()
    {
        // 真实行为是 HTTP 200 + {"info":null}，不是 404。
        var response = JsonSerializer.Deserialize<SnowLumaProbeLoginResponse>("""{"info":null}""");

        Assert.NotNull(response);
        Assert.Null(response!.Info);
    }
}
