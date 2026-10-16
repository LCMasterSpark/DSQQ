using System.IO;
using DSQQ.Models;
using DSQQ.Services;
using DSQQ.Tests.Infrastructure;

namespace DSQQ.Tests.Services;

// 一键配置里那段纯逻辑。
//
// 为什么值得单测：OneBot 端口没配好时的表现是"服务都起来了、但桥接连不上"，
// 排查起来要跨三层；而这段判断完全可以离线验证，不该留到现场去猜。
public sealed class SetupDeployerTests
{
    [Fact]
    public void 空的配置应补齐两个端口()
    {
        var config = new SnowLumaOneBotConfig();

        var changed = SetupDeployer.EnsureOneBotEndpoints(config);

        Assert.True(changed);
        Assert.Contains(config.Networks!.HttpServers, s => s.Port == SetupDeployer.OneBotHttpPort);
        Assert.Contains(config.Networks.WsServers, s => s.Port == SetupDeployer.OneBotWsPort);
    }

    [Fact]
    public void 已配好时不应改动()
    {
        var config = new SnowLumaOneBotConfig
        {
            Networks = new SnowLumaNetworks
            {
                HttpServers = [new SnowLumaServerEntry { Port = SetupDeployer.OneBotHttpPort }],
                WsServers = [new SnowLumaServerEntry { Port = SetupDeployer.OneBotWsPort }],
            },
        };

        var changed = SetupDeployer.EnsureOneBotEndpoints(config);

        // 返回 false 才能让调用方跳过回写 —— 避免无谓地整体覆盖一份好配置。
        Assert.False(changed);
        Assert.Single(config.Networks.HttpServers);
        Assert.Single(config.Networks.WsServers);
    }

    [Fact]
    public void 已有其它端口的服务应被保留()
    {
        // 用户可能自己加过服务，补端口时不能把它们的配置冲掉。
        var config = new SnowLumaOneBotConfig
        {
            Networks = new SnowLumaNetworks
            {
                HttpServers = [new SnowLumaServerEntry { Name = "自建", Port = 8080 }],
                WsServers = [],
            },
        };

        SetupDeployer.EnsureOneBotEndpoints(config);

        Assert.Contains(config.Networks.HttpServers, s => s.Port == 8080);
        Assert.Contains(config.Networks.HttpServers, s => s.Port == SetupDeployer.OneBotHttpPort);
    }

    [Fact]
    public void 只缺一个时应只补那一个()
    {
        var config = new SnowLumaOneBotConfig
        {
            Networks = new SnowLumaNetworks
            {
                HttpServers = [new SnowLumaServerEntry { Port = SetupDeployer.OneBotHttpPort }],
                WsServers = [],
            },
        };

        var changed = SetupDeployer.EnsureOneBotEndpoints(config);

        Assert.True(changed);
        Assert.Single(config.Networks.HttpServers);
        Assert.Contains(config.Networks.WsServers, s => s.Port == SetupDeployer.OneBotWsPort);
    }

    [Fact]
    public void 补出来的服务应只监听回环()
    {
        // 绑 0.0.0.0 会把 OneBot 接口暴露到局域网 —— 它没有访问令牌，
        // 等于把"用这个 QQ 号发消息"的能力对外开放。
        var config = new SnowLumaOneBotConfig();

        SetupDeployer.EnsureOneBotEndpoints(config);

        Assert.All(config.Networks!.HttpServers, s => Assert.Equal("127.0.0.1", s.Host));
        Assert.All(config.Networks.WsServers, s => Assert.Equal("127.0.0.1", s.Host));
    }

    [Fact]
    public void 补出来的WebSocket应是双向角色()
    {
        // 桥接既收事件又发消息；配成单向角色会让它收不到或发不出。
        var config = new SnowLumaOneBotConfig();

        SetupDeployer.EnsureOneBotEndpoints(config);

        Assert.All(config.Networks!.WsServers, s => Assert.Equal("Universal", s.Role));
    }

    [Fact]
    public void 消息格式应为数组()
    {
        // 桥接按数组格式解析消息段；配成 string 会让图片/表情这类消息解析失败。
        var config = new SnowLumaOneBotConfig();

        SetupDeployer.EnsureOneBotEndpoints(config);

        Assert.All(config.Networks!.HttpServers, s => Assert.Equal("array", s.MessageFormat));
        Assert.All(config.Networks.WsServers, s => Assert.Equal("array", s.MessageFormat));
    }

    [Fact]
    public void 空参数应被拒绝()
    {
        Assert.Throws<ArgumentNullException>(() => SetupDeployer.EnsureOneBotEndpoints(null!));
    }

    [Fact]
    public void 端口常量应与桥接默认指向一致()
    {
        // 这两个值是固定的，因为桥接 config.json 里默认就是
        // ws://127.0.0.1:3001 与 http://127.0.0.1:3000。
        // 一旦有人改了这里的值却没同步桥接配置，就会连不上 —— 用断言钉住。
        Assert.Equal(3000, SetupDeployer.OneBotHttpPort);
        Assert.Equal(3001, SetupDeployer.OneBotWsPort);
    }

    // ── 首次运行生成 WebUI 密码 ──

    [Fact]
    public void 全新安装应生成密码并开启注入()
    {
        // 目录里没有 config/webui.json = SnowLuma 从未配置过密码。
        using var temp = new TempDirectory();
        var config = new AppConfig();
        config.SnowLuma.InstallDirectory = temp.Path;

        var generated = SetupDeployer.EnsureBootstrapPassword(config);

        Assert.True(generated);
        Assert.True(config.SnowLuma.InjectBootstrapPassword);
        Assert.False(string.IsNullOrWhiteSpace(config.SnowLuma.BootstrapPassword));
        Assert.False(string.IsNullOrWhiteSpace(config.SnowLuma.WebUiPasswordProtected));
    }

    [Fact]
    public void 已配置过的SnowLuma不应改密码()
    {
        // 这条是安全底线：SnowLuma 已有 passwordHash 时若再注入一个新密码，
        // 用户自己记的那份就**悄悄失效**了。宁可让他手填一次。
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "config"));
        File.WriteAllText(Path.Combine(temp.Path, "config", "webui.json"), """{"passwordHash":"x"}""");

        var config = new AppConfig();
        config.SnowLuma.InstallDirectory = temp.Path;

        var generated = SetupDeployer.EnsureBootstrapPassword(config);

        Assert.False(generated);
        Assert.False(config.SnowLuma.InjectBootstrapPassword);
        Assert.Null(config.SnowLuma.BootstrapPassword);
        Assert.Null(config.SnowLuma.WebUiPasswordProtected);
    }

    [Fact]
    public void 已有密码时不应重复生成()
    {
        using var temp = new TempDirectory();
        var config = new AppConfig();
        config.SnowLuma.InstallDirectory = temp.Path;
        config.SnowLuma.WebUiPasswordProtected = "dpapi:已有";

        var generated = SetupDeployer.EnsureBootstrapPassword(config);

        Assert.False(generated);
        Assert.Equal("dpapi:已有", config.SnowLuma.WebUiPasswordProtected);
    }

    [Fact]
    public void 生成的密码应当足够长且无易混字符()
    {
        using var temp = new TempDirectory();
        var config = new AppConfig();
        config.SnowLuma.InstallDirectory = temp.Path;

        SetupDeployer.EnsureBootstrapPassword(config);
        var password = config.SnowLuma.BootstrapPassword!;

        Assert.Equal(24, password.Length);
        // 用户万一要手抄，0/O/1/I/l 这类字符最容易看错。
        Assert.DoesNotContain('0', password);
        Assert.DoesNotContain('O', password);
        Assert.DoesNotContain('1', password);
        Assert.DoesNotContain('I', password);
        Assert.DoesNotContain('l', password);
    }

    [Fact]
    public void 两次生成的密码不应相同()
    {
        using var temp = new TempDirectory();

        var first = new AppConfig();
        first.SnowLuma.InstallDirectory = temp.Path;
        SetupDeployer.EnsureBootstrapPassword(first);

        var second = new AppConfig();
        second.SnowLuma.InstallDirectory = Path.Combine(temp.Path, "another");
        Directory.CreateDirectory(second.SnowLuma.InstallDirectory);
        SetupDeployer.EnsureBootstrapPassword(second);

        Assert.NotEqual(first.SnowLuma.BootstrapPassword, second.SnowLuma.BootstrapPassword);
    }

    [Fact]
    public void 空配置应被拒绝()
    {
        Assert.Throws<ArgumentNullException>(() => SetupDeployer.EnsureBootstrapPassword(null!));
    }
}
