using System.IO;
using DSQQ.Services;
using DSQQ.Tests.Infrastructure;

namespace DSQQ.Tests.Services;

// 组件路径的自动发现。
//
// 这些用例都在**临时目录**里造结构，不去碰机器上真实装了什么 ——
// 否则同一个测试在开发机通过、在别人机器上失败，结论不可信。
public sealed class ServiceLocatorTests
{
    // ── 造结构的小工具 ──

    // 造一个"看起来像"的 SnowLuma 安装（特征文件是 index.mjs）。
    private static string MakeSnowLuma(string root, string relative)
    {
        var dir = Path.Combine(root, relative);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.mjs"), "// stub");
        return dir;
    }

    // 造一个"看起来像"的 qq-bridge（特征文件是 src/bridge.js）。
    private static string MakeBridge(string root, string relative)
    {
        var dir = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "bridge.js"), "// stub");
        return dir;
    }

    [Fact]
    public void 便携布局应被优先发现()
    {
        // {程序目录}\services\snowluma 是便携包的约定结构，
        // 也是"解压到哪都能跑"的关键 —— 它必须排在所有候选之前。
        using var temp = new TempDirectory();
        MakeSnowLuma(temp.Path, Path.Combine("app", "services", "snowluma"));

        var result = ServiceLocator.Discover(Path.Combine(temp.Path, "app"), null, null);

        Assert.NotNull(result.SnowLuma);
        Assert.Contains("services", result.SnowLuma!, StringComparison.OrdinalIgnoreCase);
        // 目录名比较不区分大小写：发现逻辑是按候选名（SnowLuma / snowluma / snow-luma）
        // 拼路径去验证的，而 Windows 文件系统本身不区分大小写，
        // 所以返回的可能是候选名写法而非磁盘上的原始写法 —— 两者指向同一目录。
        Assert.Equal("snowluma", Path.GetFileName(result.SnowLuma!), ignoreCase: true);
    }

    [Fact]
    public void 程序目录内的标准命名应被找到()
    {
        using var temp = new TempDirectory();
        MakeSnowLuma(temp.Path, Path.Combine("app", "SnowLuma"));

        var result = ServiceLocator.Discover(Path.Combine(temp.Path, "app"), null, null);

        Assert.NotNull(result.SnowLuma);
        Assert.Equal("SnowLuma", Path.GetFileName(result.SnowLuma!), ignoreCase: true);
    }

    [Fact]
    public void 父目录中的组件应被找到()
    {
        // 常见摆放：<总目录>\SnowLuma 与 <总目录>\app\DSQQ.exe
        using var temp = new TempDirectory();
        MakeSnowLuma(temp.Path, "SnowLuma");

        var result = ServiceLocator.Discover(Path.Combine(temp.Path, "app"), null, null);

        Assert.NotNull(result.SnowLuma);
        Assert.Equal(temp.Path, Path.GetDirectoryName(result.SnowLuma!));
    }

    [Fact]
    public void 缺少特征文件的同名目录不应被当成有效安装()
    {
        // 只有目录名对、里面是空的 —— 这正是"目录存在但启动失败"的来源，
        // 所以识别必须看特征文件，不能只看名字。
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "app", "SnowLuma"));

        Assert.False(ServiceLocator.IsSnowLuma(Path.Combine(temp.Path, "app", "SnowLuma")));
    }

    [Fact]
    public void 已有有效配置时应沿用而不是重新搜索()
    {
        // 用户明确指定过、且确实有效的位置优先；自动发现只补空缺。
        using var temp = new TempDirectory();
        var mine = MakeSnowLuma(temp.Path, "mine");
        MakeSnowLuma(temp.Path, Path.Combine("app", "SnowLuma"));

        var result = ServiceLocator.Discover(Path.Combine(temp.Path, "app"), mine, null);

        Assert.Equal(Path.GetFullPath(mine), result.SnowLuma, ignoreCase: true);
    }

    [Fact]
    public void 已有配置失效时应重新搜索()
    {
        // 用户挪了目录：旧路径无效时应该重新找，而不是死抱着旧值报错。
        using var temp = new TempDirectory();
        var gone = Path.Combine(temp.Path, "已删除的目录");
        MakeSnowLuma(temp.Path, Path.Combine("app", "SnowLuma"));

        var result = ServiceLocator.Discover(Path.Combine(temp.Path, "app"), gone, null);

        Assert.NotNull(result.SnowLuma);
        Assert.NotEqual(gone, result.SnowLuma);
    }

    [Fact]
    public void 桥接目录的识别应看入口脚本()
    {
        using var temp = new TempDirectory();
        var real = MakeBridge(temp.Path, Path.Combine("app", "qq-bridge"));
        Assert.True(ServiceLocator.IsBridge(real));

        // 只有个 package.json 不算 —— 那样跑不起来。
        var fake = Path.Combine(temp.Path, "fake-bridge");
        Directory.CreateDirectory(fake);
        File.WriteAllText(Path.Combine(fake, "package.json"), "{}");
        Assert.False(ServiceLocator.IsBridge(fake));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Z:\并不存在的目录\SnowLuma")]
    public void 无效路径不应被当成有效安装(string candidate)
    {
        Assert.False(ServiceLocator.IsSnowLuma(candidate));
        Assert.False(ServiceLocator.IsBridge(candidate));
    }

    [Fact]
    public void 发现结果应记录搜索过的候选目录()
    {
        // 记录它才能在界面上解释"我找过哪些地方" ——
        // 只报"没找到"会让用户无从下手。
        using var temp = new TempDirectory();

        var result = ServiceLocator.Discover(Path.Combine(temp.Path, "app"), null, null);

        Assert.NotEmpty(result.Searched);
        Assert.All(result.Searched, p => Assert.False(string.IsNullOrWhiteSpace(p)));
    }

    [Fact]
    public void 名称变体应被接受()
    {
        // 不同人解压时可能改成全小写或连字符写法。
        using var temp = new TempDirectory();
        MakeSnowLuma(temp.Path, Path.Combine("app", "snow-luma"));

        var result = ServiceLocator.Discover(Path.Combine(temp.Path, "app"), null, null);

        Assert.NotNull(result.SnowLuma);
        Assert.Equal("snow-luma", Path.GetFileName(result.SnowLuma!), ignoreCase: true);
    }

    [Fact]
    public void 空程序目录参数应被拒绝()
    {
        // 传空会让候选路径退化成相对路径，得到的"发现"毫无意义。
        Assert.Throws<ArgumentException>(() => ServiceLocator.Discover("  ", null, null));
    }

    [Fact]
    public void 两个组件都能同时被发现()
    {
        // 便携包的完整形态：services 下同时有 snowluma 与 qq-bridge。
        using var temp = new TempDirectory();
        MakeSnowLuma(temp.Path, Path.Combine("app", "services", "snowluma"));
        MakeBridge(temp.Path, Path.Combine("app", "services", "qq-bridge"));

        var result = ServiceLocator.Discover(Path.Combine(temp.Path, "app"), null, null);

        Assert.NotNull(result.SnowLuma);
        Assert.NotNull(result.Bridge);
        Assert.Equal("snowluma", Path.GetFileName(result.SnowLuma!), ignoreCase: true);
        Assert.Equal("qq-bridge", Path.GetFileName(result.Bridge!), ignoreCase: true);
    }
}
