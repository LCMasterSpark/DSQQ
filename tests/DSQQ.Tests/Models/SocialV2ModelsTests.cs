using System.Text.Json;
using DSQQ.Models;

namespace DSQQ.Tests.Models;

// 二代仿真的展示逻辑。
//
// 这些 Display 属性直接决定界面上显示什么，而原始 JSON 里全是缩写字段
//（mode / infinite / triggers / wakeSafety…）—— 拼错了用户看不懂，
// 漏了一项就会把"她为什么不说话"的关键线索藏起来。
public sealed class SocialV2ModelsTests
{
    [Fact]
    public void 会话摘要应含模式与未读数()
    {
        var conversation = new SocialV2Conversation
        {
            Key = "group:415520021",
            WakeConfig = new SocialV2WakeConfig { Mode = "diving" },
            UnreadCount = 24,
        };

        Assert.Contains("group:415520021", conversation.Display, StringComparison.Ordinal);
        Assert.Contains("[潜水]", conversation.Display, StringComparison.Ordinal);
        Assert.Contains("未读 24", conversation.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有未读时不应显示未读字样()
    {
        var conversation = new SocialV2Conversation
        {
            Key = "private:1",
            WakeConfig = new SocialV2WakeConfig { Mode = "active" },
            UnreadCount = 0,
        };

        Assert.Contains("[活跃]", conversation.Display, StringComparison.Ordinal);
        Assert.DoesNotContain("未读", conversation.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 唤醒配置应把触发条件展开成人话()
    {
        var config = new SocialV2WakeConfig
        {
            Mode = "diving",
            Infinite = true,
            WakeCount = 2,
            NoActionCount = 1,
            Triggers = new SocialV2Triggers
            {
                AtMention = true,
                NameMention = true,
                Question = true,
                Poke = true,
                Probability = 0.05,
                Keywords = ["小鲸鱼", "DeepSeek"],
            },
        };

        var text = config.Display;

        Assert.Contains("模式：潜水（无限）", text, StringComparison.Ordinal);
        Assert.Contains("@", text, StringComparison.Ordinal);
        Assert.Contains("名字", text, StringComparison.Ordinal);
        // 关键词只报个数 —— 列表可能很长，全铺开会淹没其它信息。
        Assert.Contains("关键词×2", text, StringComparison.Ordinal);
        Assert.Contains("概率 0.05", text, StringComparison.Ordinal);
        // 「唤醒多次但没动作」是触发条件过宽的直接信号，必须显示。
        Assert.Contains("唤醒 2 次，其中 1 次没动作", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 有限潜水应显示截止时间()
    {
        var config = new SocialV2WakeConfig
        {
            Mode = "diving",
            Infinite = false,
            SleepUntil = "2026-10-16T12:00:00Z",
        };

        Assert.Contains("有限", config.Display, StringComparison.Ordinal);
        Assert.Contains("2026-10-16T12:00:00Z", config.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有任何触发条件时应说明而不是留空()
    {
        var triggers = new SocialV2Triggers();

        Assert.Equal("（无）", triggers.Display);
    }

    [Fact]
    public void 安全标志应逐项列出()
    {
        var safety = new SocialV2WakeSafety { Hard = true, Guaranteed = true, Stale = true };

        var text = safety.Display;

        Assert.Contains("硬保障", text, StringComparison.Ordinal);
        Assert.Contains("保证可唤醒", text, StringComparison.Ordinal);
        // 「配置已陈旧」意味着这套唤醒条件很久没更新，可能已经不匹配当前场景。
        Assert.Contains("配置已陈旧", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有安全标志时应说明而不是留空()
    {
        Assert.Equal("（无标志）", new SocialV2WakeSafety().Display);
    }

    [Fact]
    public void 工具日志的调用条目应显示工具名与参数()
    {
        using var document = JsonDocument.Parse("""{"text":"你好"}""");
        var entry = new SocialV2ToolLogEntry
        {
            Type = "call",
            Time = "2026-10-16T11:10:39.516Z",
            Tool = "qq_send_group_message",
            Args = document.RootElement.Clone(),
        };

        var text = entry.Display;

        Assert.Contains("[调用]", text, StringComparison.Ordinal);
        Assert.Contains("qq_send_group_message", text, StringComparison.Ordinal);
        Assert.Contains("你好", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 工具日志的结果条目应区分成败()
    {
        var ok = new SocialV2ToolLogEntry { Type = "result", Tool = "t", Ok = true };
        var bad = new SocialV2ToolLogEntry { Type = "result", Tool = "t", Ok = false, Error = "超时" };

        Assert.Contains("结果 ✓", ok.Display, StringComparison.Ordinal);
        Assert.Contains("结果 ✗", bad.Display, StringComparison.Ordinal);
        // 失败原因要露出来 —— 只显示"失败"等于没给线索。
        Assert.Contains("超时", bad.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 过长的参数应被截断()
    {
        using var document = JsonDocument.Parse($$"""{"text":"{{new string('x', 500)}}"}""");
        var entry = new SocialV2ToolLogEntry
        {
            Type = "call",
            Tool = "t",
            Args = document.RootElement.Clone(),
        };

        var text = entry.Display;

        Assert.Contains("…", text, StringComparison.Ordinal);
        // 一行放得下才扫得快；完整内容仍可从日志文件取。
        Assert.True(text.Length < 260, $"应被截断，实际长度 {text.Length}");
    }

    [Fact]
    public void 时间无法解析时不应抛错()
    {
        var entry = new SocialV2ToolLogEntry { Type = "call", Time = "不是时间", Tool = "t" };

        Assert.Contains("t", entry.Display, StringComparison.Ordinal);
    }
}
