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
    public void QqGroupList_应能解析群列表真实响应()
    {
        // 取自桥接 GET /api/qq/groups 的真实结构（注意是 id/name，不是 groupId/groupName）。
        const string json = """
        {
          "ok": true,
          "groups": [
            { "id": "1056515304", "name": "魅魔饭店" },
            { "id": "415520021", "name": "TUTE幻想乡东方project同好会" },
            { "id": "1103374910", "name": "云汀柔045c" }
          ]
        }
        """;

        var response = JsonSerializer.Deserialize<QqGroupList>(json, Options);

        Assert.NotNull(response);
        Assert.Equal(3, response!.Groups.Count);
        Assert.Equal("1056515304", response.Groups[0].Id);
        Assert.Equal("魅魔饭店", response.Groups[0].Name);
        Assert.Equal("TUTE幻想乡东方project同好会", response.Groups[1].Name);
    }

    [Fact]
    public void QqGroupList_群列表为空时不应抛错()
    {
        // 账号未登录时桥接会返回 ok=false 且带 error，界面必须能安全处理这种"没有数据"的情况。
        const string json = """{ "ok": false, "groups": [], "error": "fetch failed" }""";

        var response = JsonSerializer.Deserialize<QqGroupList>(json, Options);

        Assert.NotNull(response);
        Assert.Empty(response!.Groups);
    }

    [Fact]
    public void 群列表项_Display应同时包含群名与群号()
    {
        // 白名单页的 ListBox 是 DisplayMemberPath="Display"，而列表里存放的是
        // BridgeConsoleView.GroupItem（不是数据模型 QqGroup）。
        // 这里钉住那个类型确实提供了 Display —— 它若缺失，绑定会静默失败，
        // 界面表现是每一项都渲染成空白行，而不是报错。
        var item = new DSQQ.Views.BridgeConsoleView.GroupItem("1056515304", "魅魔饭店");

        Assert.Contains("魅魔饭店", item.Display, StringComparison.Ordinal);
        Assert.Contains("1056515304", item.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 活跃度预设_必须满足桥接的范围与联动约束()
    {
        // 桥接对 /api/config/activity 有一套范围校验 + 四条联动校验，
        // 任一不满足就整档返回 400。预设是写死的数据，最容易写错又看不出来，
        // 所以这里把桥接的规则抄过来逐个核对。
        // 键名 → (最小值, 最大值)
        var ranges = new Dictionary<string, (int Min, int Max)>(StringComparer.Ordinal)
        {
            ["sleepMinMs"] = (0, 86_400_000),
            ["sleepMaxMs"] = (0, 86_400_000),
            ["recommendedSleepMinMs"] = (1_000, 86_400_000),
            ["recommendedSleepMaxMs"] = (1_000, 86_400_000),
            ["maxWakePerHour"] = (0, 1_000),
            ["maxWakePerMinute"] = (0, 100),
            ["preSleepWaitMs"] = (0, 3_600_000),
            ["batchWindowMs"] = (1_000, 600_000),
            ["autoReplyCheckMs"] = (1_000, 600_000),
            ["maxSendPerHour"] = (0, 10_000),
            ["maxSendPerMinute"] = (0, 1_000),
            ["burstIntervalMinMs"] = (0, 600_000),
            ["burstIntervalMaxMs"] = (0, 600_000),
            ["gapBaseMs"] = (0, 600_000),
            ["gapPerCharMs"] = (0, 1_000),
            ["maxMessageChars"] = (1, 5_000),
        };

        Assert.Equal(3, DSQQ.Models.ActivityPresets.All.Count);

        foreach (var preset in DSQQ.Models.ActivityPresets.All)
        {
            // 每个字段都要在范围内
            foreach (var (key, value) in preset.Values)
            {
                Assert.True(ranges.ContainsKey(key), $"预设「{preset.Name}」含未知字段 {key}");
                var (min, max) = ranges[key];
                Assert.True(
                    value >= min && value <= max,
                    $"预设「{preset.Name}」的 {key}={value} 超出桥接允许的 {min}~{max}");
            }

            // 四条联动校验（与桥接的判定逐条对应）
            Assert.True(
                preset.Values["sleepMaxMs"] <= 0
                    || preset.Values["sleepMinMs"] <= preset.Values["sleepMaxMs"],
                $"预设「{preset.Name}」最短潜水大于最长潜水");

            Assert.True(
                preset.Values["recommendedSleepMinMs"] <= preset.Values["recommendedSleepMaxMs"],
                $"预设「{preset.Name}」推荐最短大于推荐最长");

            Assert.True(
                preset.Values["burstIntervalMinMs"] <= preset.Values["burstIntervalMaxMs"],
                $"预设「{preset.Name}」连发最短间隔大于最长间隔");

            Assert.True(
                preset.Values["sleepMaxMs"] <= 0
                    || preset.Values["recommendedSleepMaxMs"] <= preset.Values["sleepMaxMs"],
                $"预设「{preset.Name}」推荐最长超过最长潜水");
        }
    }

    [Fact]
    public void 活跃度预设_每档覆盖同一组字段()
    {
        // 套用预设时是按键写入输入框的；若某档缺了字段，那一项就会**保留上一档的值**，
        // 而不是被重置 —— 用户看到的会是"混了两档"的配置，且没有任何提示。
        var expected = DSQQ.Models.ActivityPresets.All[0].Values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        foreach (var preset in DSQQ.Models.ActivityPresets.All)
        {
            var keys = preset.Values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, keys);
        }

        // 同时确认没有漏掉"可编辑字段"里的项（maxWakePerMinute 等必须在预设里出现）。
        Assert.Contains("maxMessageChars", expected);
        Assert.Contains("maxWakePerMinute", expected);
        Assert.Contains("recommendedSleepMinMs", expected);
    }

    [Fact]
    public void 活跃度预设_由高到低排列()
    {
        // 用户预期「高频 → 标准 → 低频」是逐渐安静的，这靠两个关键指标就能判定。
        var presets = DSQQ.Models.ActivityPresets.All;

        Assert.Equal("高频", presets[0].Name);
        Assert.Equal("标准", presets[1].Name);
        Assert.Equal("低频", presets[2].Name);

        for (var i = 1; i < presets.Count; i++)
        {
            Assert.True(
                presets[i].Values["maxWakePerHour"] < presets[i - 1].Values["maxWakePerHour"],
                $"「{presets[i].Name}」的每小时唤醒上限应低于「{presets[i - 1].Name}」");

            Assert.True(
                presets[i].Values["sleepMinMs"] > presets[i - 1].Values["sleepMinMs"],
                $"「{presets[i].Name}」的最短潜水应长于「{presets[i - 1].Name}」");
        }
    }

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
