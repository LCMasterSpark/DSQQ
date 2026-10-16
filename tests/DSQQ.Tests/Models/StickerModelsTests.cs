using System.Text.Json;
using DSQQ.Models;

namespace DSQQ.Tests.Models;

// 表情库与 AI 反馈的展示逻辑。
//
// 表情库的原始条目有四种寻址方式（id/resId/md5/url）和四个描述字段
//（desc/localNote/tags/usage），界面上必须挑对：挑错了要么显示不出来，
// 要么用户改了半天发现保存的是另一个字段。
public sealed class StickerModelsTests
{
    [Fact]
    public void 表情列表应按桥接的真实形状解析()
    {
        // 形状取自 formatStickerList 的返回值（sticker-lib.js）。
        const string json = """
        {
          "ok": true,
          "total": 12,
          "matched": 3,
          "truncated": true,
          "stickers": [
            {
              "id": "1001",
              "resId": "res-1001",
              "md5": "ABCDEF",
              "url": "http://example.invalid/a.png",
              "desc": "QQ 备注",
              "localNote": "本地笔记",
              "tags": ["嘲讽", "笑哭"],
              "usage": "怼人的时候用",
              "useCount": 7,
              "lastUsedAt": 1792156535736,
              "source": "qq"
            }
          ],
          "syncedAt": 1792156535736,
          "fromCache": false
        }
        """;

        var result = JsonSerializer.Deserialize<StickerListResponse>(json)!;

        Assert.True(result.Ok);
        Assert.Equal(12, result.Total);
        Assert.Equal(3, result.Matched);
        Assert.True(result.Truncated);
        Assert.False(result.FromCache);

        var sticker = Assert.Single(result.Stickers);
        Assert.Equal("1001", sticker.Id);
        Assert.Equal("res-1001", sticker.ResId);
        Assert.Equal("ABCDEF", sticker.Md5);
        Assert.Equal("QQ 备注", sticker.Desc);
        Assert.Equal("本地笔记", sticker.LocalNote);
        Assert.Equal(7, sticker.UseCount);
        Assert.Equal(2, sticker.Tags!.Count);
    }

    [Fact]
    public void 本地笔记应优先于QQ备注()
    {
        var sticker = new StickerEntry { Desc = "QQ 备注", LocalNote = "本地笔记" };

        Assert.Contains("本地笔记", sticker.Display, StringComparison.Ordinal);
        Assert.DoesNotContain("QQ 备注", sticker.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有本地笔记时退回QQ备注()
    {
        var sticker = new StickerEntry { Desc = "QQ 备注" };

        Assert.Contains("QQ 备注", sticker.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 两种备注都没有时给出可操作的提示()
    {
        // 空着不说明该做什么，而"可先看图"正是下一步动作。
        var sticker = new StickerEntry();

        Assert.Contains("可先看图", sticker.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 标签与使用次数应进显示行()
    {
        var sticker = new StickerEntry
        {
            LocalNote = "好图",
            Tags = ["嘲讽", "笑哭"],
            UseCount = 3,
        };

        Assert.Contains("[嘲讽/笑哭]", sticker.Display, StringComparison.Ordinal);
        Assert.Contains("用过 3 次", sticker.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 没用过的表情不应显示使用次数()
    {
        var sticker = new StickerEntry { LocalNote = "好图", UseCount = 0 };

        Assert.DoesNotContain("用过", sticker.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 过长的用法说明应被截断()
    {
        // 完整内容在右侧编辑框里，列表行只需够认出来。
        var sticker = new StickerEntry
        {
            LocalNote = "好图",
            Usage = new string('长', 60),
        };

        Assert.Contains("…", sticker.Display, StringComparison.Ordinal);
        Assert.True(sticker.Display.Length < 60);
    }

    [Fact]
    public void 用法说明里的换行不应把列表行撑成多行()
    {
        var sticker = new StickerEntry { LocalNote = "好图", Usage = "第一行\n第二行" };

        Assert.DoesNotContain("\n", sticker.Display, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id", "res", "MD5", "url", "id")]
    [InlineData(null, "res", "MD5", "url", "res")]
    [InlineData(null, null, "MD5", "url", "MD5")]
    [InlineData(null, null, null, "url", "url")]
    public void 匹配标识应按id到url的顺序取第一个非空(
        string? id, string? resId, string? md5, string? url, string expected)
    {
        // 顺序与上游 findStickerEntry 的匹配顺序一致 —— 否则保存会打到另一张图上。
        var sticker = new StickerEntry { Id = id, ResId = resId, Md5 = md5, Url = url };

        Assert.Equal(expected, sticker.MatchId);
    }

    [Fact]
    public void 四种标识都缺失时匹配标识应为空()
    {
        // 界面据此拒绝保存；返回 null 会让调用方更难判断。
        Assert.Equal(string.Empty, new StickerEntry().MatchId);
    }

    [Fact]
    public void 空白标识不算有效标识()
    {
        var sticker = new StickerEntry { Id = "   ", ResId = "res" };

        Assert.Equal("res", sticker.MatchId);
    }

    [Fact]
    public void 标签串应空格分隔()
    {
        var sticker = new StickerEntry { Tags = ["嘲讽", "笑哭"] };

        Assert.Equal("嘲讽 笑哭", sticker.TagsText);
    }

    [Fact]
    public void 没有标签时标签串为空()
    {
        Assert.Equal(string.Empty, new StickerEntry().TagsText);
    }

    [Fact]
    public void 保存响应应带回保存后的条目()
    {
        const string json = """
        { "ok": true, "sticker": { "id": "1001", "localNote": "改过了", "tags": ["新标签"] } }
        """;

        var saved = JsonSerializer.Deserialize<StickerSaveResponse>(json)!;

        Assert.True(saved.Ok);
        Assert.Equal("改过了", saved.Sticker!.LocalNote);
        Assert.Equal(["新标签"], saved.Sticker.Tags!);
    }

    [Fact]
    public void 保存失败时错误原因不应丢失()
    {
        // 桥接把失败原因放在 error 里，丢掉它用户只能看到"失败了"。
        const string json = """{ "ok": false, "error": "找不到表情 nope" }""";

        var saved = JsonSerializer.Deserialize<StickerSaveResponse>(json)!;

        Assert.False(saved.Ok);
        Assert.Equal("找不到表情 nope", saved.Error);
    }

    [Theory]
    [InlineData("error", "✗")]
    [InlineData("warning", "!")]
    [InlineData("info", "·")]
    [InlineData(null, "·")]
    public void 反馈条目应按级别给出符号(string? level, string mark)
    {
        var entry = new SocialV2FeedbackEntry { Level = level, Message = "内容" };

        Assert.StartsWith(mark, entry.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 反馈条目应含会话与时间()
    {
        var entry = new SocialV2FeedbackEntry
        {
            Key = "group:415520021",
            Level = "info",
            Message = "有点困惑",
            Time = "2026-10-15T13:00:00.000Z",
        };

        Assert.Contains("group:415520021", entry.Display, StringComparison.Ordinal);
        Assert.Contains("有点困惑", entry.Display, StringComparison.Ordinal);

        // 时间应被转成本地可读形式，而不是原样抛出 ISO 串。
        Assert.DoesNotContain("2026-10-15T13:00:00", entry.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 反馈时间无法解析时不应抛错()
    {
        // 一条脏数据不能让整页空白 —— 这正是最需要看到反馈的时候。
        var entry = new SocialV2FeedbackEntry { Message = "内容", Time = "不是时间" };

        Assert.Contains("无法解析", entry.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 反馈正文里的换行应被压平()
    {
        // 一条反馈固定占两行（标题行 + 正文行）。正文里的换行必须压平，
        // 否则"一共有几条反馈"就看不出来了。
        var entry = new SocialV2FeedbackEntry { Message = "第一行\n第二行\r\n第三行" };

        var lines = entry.Display.Split('\n');
        Assert.Equal(2, lines.Length);

        // 逐字断言：既验证压平，也验证 "\r\n" 没有留下两个连续空格。
        Assert.Equal("第一行 第二行 第三行", lines[1].Trim());
    }

    [Fact]
    public void 反馈缺少会话与正文时应给出兜底文案()
    {
        var entry = new SocialV2FeedbackEntry();

        Assert.Contains("（无会话）", entry.Display, StringComparison.Ordinal);
        Assert.Contains("（无内容）", entry.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 反馈列表应按桥接的真实形状解析()
    {
        const string json = """
        {
          "ok": true,
          "entries": [
            { "id": "abc-123", "key": "group:1", "level": "error", "message": "出错了", "time": "2026-10-15T13:00:00.000Z" }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<SocialV2FeedbackResponse>(json)!;

        var entry = Assert.Single(result.Entries);
        Assert.Equal("abc-123", entry.Id);
        Assert.Equal("group:1", entry.Key);
        Assert.Equal("error", entry.Level);
        Assert.Equal("出错了", entry.Message);
    }
}
