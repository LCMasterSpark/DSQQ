using System.Text.Json;
using DSQQ.Models;

namespace DSQQ.Tests.Models;

// P5 新增模型的解析与展示逻辑。
// 这些用例钉住的是"界面直接依赖的计算属性"（Display/IsCandidate/Truncated 标记），
// 它们一旦与后端字段名脱节，界面会静默显示错误信息而不是报错。
public sealed class BridgeModelsTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void RoleListResponse_应能解析真实响应形状()
    {
        // 取自桥接 /api/roles 的真实结构。
        const string json = """
        {
          "roles": ["傲娇助手", "小鲸鱼"],
          "summaries": [
            { "name": "傲娇助手", "bytes": 497, "chars": 191, "injectedV1": 190, "injectedV2": 190, "truncated": false, "warnings": 0, "excerpt": "傲娇助手" },
            { "name": "小鲸鱼", "bytes": 12907, "chars": 4995, "injectedV1": 4994, "injectedV2": 4994, "truncated": true, "warnings": 1, "excerpt": "角色卡" }
          ],
          "current": null,
          "limits": { "nameMax": 40, "contentMaxBytes": 65536, "injectMaxChars": 6000 }
        }
        """;

        var response = JsonSerializer.Deserialize<RoleListResponse>(json, Options);

        Assert.NotNull(response);
        Assert.Equal(2, response!.Roles.Count);
        Assert.Equal(2, response.Summaries.Count);
        Assert.Null(response.Current);
        Assert.Equal(6000, response.Limits!.InjectMaxChars);

        var role = response.Summaries[1];
        Assert.Equal("小鲸鱼", role.Name);
        Assert.Equal(4995, role.Chars);
        Assert.True(role.Truncated);
        Assert.Equal(1, role.Warnings);
    }

    [Fact]
    public void RoleSummary_Display应同时体现字数与告警()
    {
        var normal = new RoleSummary { Name = "傲娇助手", Chars = 191, InjectedV2 = 190 };
        Assert.Contains("191 字", normal.Display, StringComparison.Ordinal);
        Assert.DoesNotContain("超上限", normal.Display, StringComparison.Ordinal);
        Assert.DoesNotContain("告警", normal.Display, StringComparison.Ordinal);

        // 超限与告警必须出现在列表上：用户不该点进去才发现角色不可用。
        var problematic = new RoleSummary
        {
            Name = "小鲸鱼",
            Chars = 4995,
            InjectedV2 = 6000,
            Truncated = true,
            Warnings = 1,
        };

        Assert.Contains("超上限", problematic.Display, StringComparison.Ordinal);
        Assert.Contains("1 项告警", problematic.Display, StringComparison.Ordinal);
        Assert.Contains("小鲸鱼", problematic.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void SlangEntry_IsCandidate应识别候选状态()
    {
        Assert.True(new SlangEntry { Status = "candidate" }.IsCandidate);
        Assert.True(new SlangEntry { Status = "CANDIDATE" }.IsCandidate);
        Assert.False(new SlangEntry { Status = "confirmed" }.IsCandidate);
        Assert.False(new SlangEntry { Status = null }.IsCandidate);
    }

    [Fact]
    public void SlangEntry_Display应带状态与含义()
    {
        var withMeaning = new SlangEntry
        {
            Content = "不要笑挑战",
            Status = "candidate",
            Meaning = "群里的一种互动玩法",
            Count = 3,
        };

        Assert.Contains("[candidate]", withMeaning.Display, StringComparison.Ordinal);
        Assert.Contains("不要笑挑战", withMeaning.Display, StringComparison.Ordinal);
        Assert.Contains("群里的一种互动玩法", withMeaning.Display, StringComparison.Ordinal);
        Assert.Contains("3 次", withMeaning.Display, StringComparison.Ordinal);

        // 含义为空时不应留下一个孤零零的分隔符。
        var noMeaning = new SlangEntry { Content = "部长的事别管", Status = "candidate" };
        Assert.DoesNotContain("——", noMeaning.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void SlangResponse_应能解析真实响应形状()
    {
        const string json = """
        {
          "entries": [
            {
              "id": "mv9lqlr7-krtlvf",
              "content": "不要笑挑战",
              "meaning": "",
              "status": "candidate",
              "source": "ai",
              "count": 1
            }
          ]
        }
        """;

        var response = JsonSerializer.Deserialize<SlangResponse>(json, Options);

        Assert.NotNull(response);
        var entry = Assert.Single(response!.Entries);
        Assert.Equal("mv9lqlr7-krtlvf", entry.Id);
        Assert.True(entry.IsCandidate);
        Assert.Equal(1, entry.Count);
    }

    [Fact]
    public void SimPromptResponse_应能解析预设状态()
    {
        const string json = """
        {
          "ok": true,
          "content": "You are a coding agent...",
          "status": {
            "preset": "qq-chat-v2",
            "label": "二代仿真（reserved2）",
            "installed": true,
            "requiresRestart": true
          }
        }
        """;

        var response = JsonSerializer.Deserialize<SimPromptResponse>(json, Options);

        Assert.NotNull(response);
        Assert.Equal("qq-chat-v2", response!.Status!.Preset);
        Assert.True(response.Status.RequiresRestart);
        Assert.StartsWith("You are", response.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionModeResponse_应能解析会话统计()
    {
        const string json = """
        {
          "ok": true,
          "sessionMode": "per-conversation",
          "ownerKey": "group:481718944",
          "conversationCount": 3,
          "sessionCount": 3,
          "sharedSessions": null
        }
        """;

        var response = JsonSerializer.Deserialize<SessionModeResponse>(json, Options);

        Assert.NotNull(response);
        Assert.Equal("per-conversation", response!.SessionMode);
        Assert.Equal(3, response.ConversationCount);
        Assert.Equal(3, response.SessionCount);
    }

    [Fact]
    public void 未知字段不应导致解析失败()
    {
        // 桥接后续版本可能新增字段；这里保证界面不会因为多出来的字段就整页读取失败。
        const string json = """
        {
          "roles": [],
          "summaries": [],
          "current": "小鲸鱼",
          "brandNewField": { "nested": [1, 2, 3] }
        }
        """;

        var response = JsonSerializer.Deserialize<RoleListResponse>(json, Options);

        Assert.NotNull(response);
        Assert.Equal("小鲸鱼", response!.Current);
    }
}
