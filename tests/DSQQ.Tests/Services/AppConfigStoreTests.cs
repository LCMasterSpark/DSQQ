using System.IO;
using DSQQ.Models;
using DSQQ.Services;
using DSQQ.Tests.Infrastructure;

namespace DSQQ.Tests.Services;

// AppConfigStore 的行为约束。
// 这些用例钉住的是"配置坏掉时程序仍能启动"这一健壮性要求：
// 文件缺失、内容为空、JSON 语法错误、字段类型不符，全部只能返回 null + 原因，不得抛异常。
public sealed class AppConfigStoreTests
{
    [Fact]
    public void TryLoad_文件不存在_应返回空并给出提示()
    {
        using var temp = new TempDirectory();

        var config = AppConfigStore.TryLoad(temp.File("not-created.json"), out var error);

        Assert.Null(config);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryLoad_空文件_应返回空而非抛异常()
    {
        using var temp = new TempDirectory();
        var path = temp.File("empty.json");
        File.WriteAllText(path, string.Empty);

        var config = AppConfigStore.TryLoad(path, out var error);

        Assert.Null(config);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryLoad_损坏的JSON_应返回空而非抛异常()
    {
        // 这是最容易发生的一种损坏：用户手工编辑配置时漏了逗号或引号。
        using var temp = new TempDirectory();
        var path = temp.File("broken.json");
        File.WriteAllText(path, "{ \"schemaVersion\": 1, ");

        var config = AppConfigStore.TryLoad(path, out var error);

        Assert.Null(config);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryLoad_字段类型不符_应返回空而非抛异常()
    {
        using var temp = new TempDirectory();
        var path = temp.File("wrong-type.json");
        // consolePort 期望是数字，这里给了字符串。
        File.WriteAllText(path, """{ "bridge": { "consolePort": "三千一百" } }""");

        var config = AppConfigStore.TryLoad(path, out var error);

        Assert.Null(config);
        Assert.NotNull(error);
    }

    [Fact]
    public void Save然后TryLoad_应完整往返()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");

        var original = AppConfig.CreateDefault();
        original.SnowLuma.InstallDirectory = @"D:\SnowLuma";
        original.SnowLuma.PreferredWebUiPort = 5099;
        original.Bridge.ConsolePort = 3100;
        original.Bridge.Directory = @"D:\qq-bridge";
        original.Dsh.Port = 19387;
        original.Monitoring.PollIntervalMs = 2500;

        AppConfigStore.Save(original, path);
        var loaded = AppConfigStore.TryLoad(path, out var error);

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(@"D:\SnowLuma", loaded!.SnowLuma.InstallDirectory);
        Assert.Equal(5099, loaded.SnowLuma.PreferredWebUiPort);
        Assert.Equal(3100, loaded.Bridge.ConsolePort);
        Assert.Equal(@"D:\qq-bridge", loaded.Bridge.Directory);
        Assert.Equal(19387, loaded.Dsh.Port);
        Assert.Equal(2500, loaded.Monitoring.PollIntervalMs);
    }

    [Fact]
    public void Save_未知字段应被保留_避免新旧版本互相丢配置()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");

        // 模拟"更高版本写出的配置"被当前版本读入后再保存。
        File.WriteAllText(path, """{ "futureOption": { "enabled": true }, "schemaVersion": 1 }""");

        var loaded = AppConfigStore.TryLoad(path, out _);
        Assert.NotNull(loaded);

        AppConfigStore.Save(loaded!, path);
        var text = File.ReadAllText(path);

        Assert.Contains("futureOption", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_应创建尚不存在的目录()
    {
        using var temp = new TempDirectory();
        var nested = temp.File(Path.Combine("a", "b", "config.json"));

        AppConfigStore.Save(AppConfig.CreateDefault(), nested);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void CreateDefault_应给出一套可用的默认值()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(5099, config.SnowLuma.PreferredWebUiPort);
        Assert.Equal(3100, config.Bridge.ConsolePort);
        // DSH 桌面版实测端口是 19387，而不是文档里写的 3080。
        Assert.Equal(19387, config.Dsh.Port);
        Assert.NotEmpty(config.Monitoring.AlertKeywords);
        Assert.True(config.Startup.LaunchBridge);
        // 合规默认值：不得默认替用户自动登录 SnowLuma。
        Assert.False(config.SnowLuma.InjectBootstrapPassword);
    }
}
