using DSQQ.Models;

namespace DSQQ.Tests.Models;

// 「她是谁」三层的一致性审计。
//
// 这些用例钉住的是**冲突的识别**：三层同时进上下文却没有任何自动仲裁，
// 出了问题只会表现为"她今天怎么怪怪的"，很难倒查到是配置冲突。
public sealed class IdentityAuditorTests
{
    // 角色卡正文里带身份表述（会与人设的 name 冲突）。
    private const string RoleWithSelfName = """
        # 角色卡：小鲸鱼
        ## 一、你是谁
        自我认知是「DeepSeek 娘 / 小鲸鱼」。
        """;

    // 角色卡正文里带一代指令（在二代下会与工具协议冲突）。
    private const string RoleWithGen1 = """
        # 角色卡
        用空格分句，输出 [SILENT] 表示保持安静。
        """;

    private static IdentityAuditor.Input Make(
        bool personaEnabled = false,
        string personaName = "",
        string? roleContent = null,
        string? simPrompt = "预设提示词内容",
        string? mode = "reserved2",
        int slangChars = 0)
        => new(
            Persona: new PersonaConfig { Enabled = personaEnabled },
            PersonaValues: new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = personaName },
            RoleName: roleContent is null ? null : "小鲸鱼",
            RoleContent: roleContent,
            SimPrompt: simPrompt,
            SimPromptPreset: "qq-chat-v2",
            Mode: mode,
            SlangChars: slangChars);

    [Fact]
    public void 人设与角色卡同时定义名字时应告警()
    {
        var report = IdentityAuditor.Audit(Make(
            personaEnabled: true, personaName: "肥鱼", roleContent: RoleWithSelfName));

        Assert.Contains(report.Warnings, w => w.Contains("叫什么", StringComparison.Ordinal));
    }

    [Fact]
    public void 人设未启用时不应因人设而告警()
    {
        // 人设没启用，它填的内容不会进上下文，不该报冲突。
        var report = IdentityAuditor.Audit(Make(
            personaEnabled: false, personaName: "肥鱼", roleContent: RoleWithSelfName));

        Assert.DoesNotContain(report.Warnings, w => w.Contains("叫什么", StringComparison.Ordinal));
    }

    [Fact]
    public void 角色卡没在定义身份时不应告警()
    {
        var report = IdentityAuditor.Audit(Make(
            personaEnabled: true, personaName: "肥鱼", roleContent: "# 角色卡\n只讲说话方式，不谈身份。"));

        Assert.DoesNotContain(report.Warnings, w => w.Contains("叫什么", StringComparison.Ordinal));
    }

    [Fact]
    public void 二代模式下角色卡含一代指令应告警()
    {
        var report = IdentityAuditor.Audit(Make(roleContent: RoleWithGen1, mode: "reserved2"));

        var warning = Assert.Single(report.Warnings, w => w.Contains("一代专用指令", StringComparison.Ordinal));
        // 提示里要点出最危险的那一项，用户才知道后果的严重程度。
        Assert.Contains("[SILENT]", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void 一代模式下同样的角色卡不应告警()
    {
        // chat 是一代仿真，那些标记本来就是正常用法 —— 误报会让用户不再信任提示。
        var report = IdentityAuditor.Audit(Make(roleContent: RoleWithGen1, mode: "chat"));

        Assert.DoesNotContain(report.Warnings, w => w.Contains("一代专用指令", StringComparison.Ordinal));
    }

    [Fact]
    public void 人设字段超出桥接上限时应告警()
    {
        var input = new IdentityAuditor.Input(
            Persona: new PersonaConfig { Enabled = true },
            PersonaValues: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // bridge.js 对 tone 的上限是 200
                ["tone"] = new string('啊', 201),
            },
            RoleName: null, RoleContent: null,
            SimPrompt: "x", SimPromptPreset: "qq-chat-v2",
            Mode: "reserved2", SlangChars: 0);

        var report = IdentityAuditor.Audit(input);

        Assert.Contains(report.Warnings, w => w.Contains("200", StringComparison.Ordinal));
    }

    [Fact]
    public void 三层全空时应告警()
    {
        var report = IdentityAuditor.Audit(Make(simPrompt: null));

        Assert.Contains(report.Warnings, w => w.Contains("三层都是空", StringComparison.Ordinal));
    }

    [Fact]
    public void 干净配置不应产生任何告警()
    {
        var report = IdentityAuditor.Audit(Make(
            personaEnabled: true, personaName: "肥鱼",
            roleContent: "# 角色卡\n只描述说话习惯。", mode: "reserved2"));

        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void 预算应统计各层字符数()
    {
        var input = new IdentityAuditor.Input(
            Persona: new PersonaConfig { Enabled = true },
            PersonaValues: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = "肥鱼",       // 2
                ["tone"] = "懒",         // 1
            },
            RoleName: "小鲸鱼",
            RoleContent: new string('卡', 500),
            SimPrompt: new string('提', 1000),
            SimPromptPreset: "qq-chat-v2",
            Mode: "reserved2",
            SlangChars: 40);

        var report = IdentityAuditor.Audit(input);

        Assert.Equal(3, report.Budget.PersonaChars);
        Assert.Equal(500, report.Budget.RoleChars);
        Assert.Equal(1000, report.Budget.SimPromptChars);
        Assert.Equal(40, report.Budget.SlangChars);
        Assert.Equal(1543, report.Budget.TotalChars);
    }

    [Fact]
    public void 人设未启用时预算应为零()
    {
        var report = IdentityAuditor.Audit(Make(
            personaEnabled: false, personaName: "肥鱼", roleContent: "# 卡"));

        // 未启用的人设不进上下文，不该占用预算 —— 否则用户会误以为它在耗 token。
        Assert.Equal(0, report.Budget.PersonaChars);
    }

    [Fact]
    public void 人设总上限应等于各字段上限之和()
    {
        Assert.Equal(2040, IdentityAuditor.PersonaTotalLimit);
        Assert.Equal(2040, IdentityAuditor.PersonaFieldLimits.Values.Sum());
    }

    [Theory]
    [InlineData("reserved2", true)]
    [InlineData("chat", false)]
    [InlineData("reserved", false)]
    [InlineData(null, false)]
    public void 二代判定(string? mode, bool expected)
    {
        Assert.Equal(expected, IdentityAuditor.IsSecondGeneration(mode));
    }

    [Theory]
    [InlineData("你是谁", true)]
    [InlineData("自我认知是...", true)]
    [InlineData("自称小鲸鱼", true)]
    [InlineData("只讲说话方式", false)]
    public void 身份表述识别(string content, bool expected)
    {
        Assert.Equal(expected, IdentityAuditor.MentionsSelfName(content));
    }
}
