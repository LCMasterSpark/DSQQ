using System.Text.Json;
using DSQQ.Models;
using Xunit;

namespace DSQQ.Tests.Models;

// 语音库的模型层测试。
//
// 这些用例的 JSON 片段全部来自桥接的真实响应（见 .dlsrc 下 2026-10-16 的实测记录），
// 不是照着自己的模型反推出来的 —— 反推出来的样本只能证明"我写的东西自洽"。
public class VoiceModelsTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // 桥接实测：GET /api/voice/list（控制台通道，库里有 1 个文件）
    private const string ListJson = """
        {
          "ok": true,
          "key": null,
          "dir": "D:\\qq-bridge\\audio",
          "count": 1,
          "voices": [
            { "name": "__dlsrc_probe.wav", "ok": true, "bytes": 32044, "detail": "31.3 KB", "seconds": null }
          ],
          "limits": { "maxSeconds": 300, "maxBytes": 20971520, "maxPerMinute": 2, "maxPerHour": 10 },
          "hint": "用 qq_send_voice 传 name 即可把其中一个当语音发出去"
        }
        """;

    [Fact]
    public void 列表响应能按真实形状解析()
    {
        var list = JsonSerializer.Deserialize<VoiceListResponse>(ListJson, Options);

        Assert.NotNull(list);
        Assert.True(list!.Ok);
        Assert.Equal(@"D:\qq-bridge\audio", list.Dir);
        Assert.Equal(1, list.Count);
        Assert.Single(list.Voices);
        Assert.Equal("__dlsrc_probe.wav", list.Voices[0].Name);
        Assert.Equal(32044, list.Voices[0].Bytes);
        Assert.Equal("31.3 KB", list.Voices[0].Detail);
        Assert.Null(list.Voices[0].Seconds);
        Assert.Equal(300, list.Limits!.MaxSeconds);
        Assert.Equal(20971520, list.Limits.MaxBytes);
        Assert.Equal(2, list.Limits.MaxPerMinute);
        Assert.Equal(10, list.Limits.MaxPerHour);
    }

    [Fact]
    public void 空库不是失败_并且带回桥接那句带目录的提示()
    {
        var json = """
            { "ok": true, "key": null, "dir": "D:\\qq-bridge\\audio", "count": 0, "voices": [],
              "limits": { "maxSeconds": 300, "maxBytes": 20971520, "maxPerMinute": 2, "maxPerHour": 10 },
              "hint": "语音库为空：把音频文件放进 D:\\qq-bridge\\audio 后重试" }
            """;

        var list = JsonSerializer.Deserialize<VoiceListResponse>(json, Options);

        Assert.NotNull(list);
        // 「库是空的」和「读失败了」是两回事，界面上给的处理完全不同。
        Assert.True(list!.Ok);
        Assert.Equal(0, list.Count);
        Assert.Empty(list.Voices);
        Assert.Contains("D:\\qq-bridge\\audio", list.Hint);
    }

    [Fact]
    public void 读取失败时带回原因()
    {
        var json = """{ "ok": false, "error": "读取语音库失败：EACCES" }""";

        var list = JsonSerializer.Deserialize<VoiceListResponse>(json, Options);

        Assert.NotNull(list);
        Assert.False(list!.Ok);
        Assert.Equal("读取语音库失败：EACCES", list.Error);
    }

    [Fact]
    public void 条目的显示行_能发的带对勾和大小()
    {
        var entry = new VoiceEntry { Name = "早安.wav", Ok = true, Detail = "31.3 KB" };

        Assert.Equal("✓ 早安.wav　31.3 KB", entry.Display);
    }

    [Fact]
    public void 条目的显示行_有时长就写出来()
    {
        var entry = new VoiceEntry { Name = "早安.wav", Ok = true, Detail = "1.2 MB", Seconds = 3.24 };

        // 秒数保留一位小数：桥接给的精度远高于此，界面写全了没人看。
        Assert.Equal("✓ 早安.wav　1.2 MB　3.2 秒", entry.Display);
    }

    [Fact]
    public void 条目的显示行_探测失败时把原因写在行里()
    {
        var entry = new VoiceEntry { Name = "坏了.wav", Ok = false, Error = "不是可识别的音频" };

        // 不能只画个叉 —— 用户下一步要做的就是"为什么不能发"。
        Assert.Equal("✗ 坏了.wav　（不是可识别的音频）", entry.Display);
    }

    [Fact]
    public void 上限能写成人话()
    {
        var limits = new VoiceLimits { MaxSeconds = 300, MaxBytes = 20971520, MaxPerMinute = 2, MaxPerHour = 10 };

        Assert.Equal("单条最长 5 分钟　·　最大 20 MB　·　每分钟最多 2 条　·　每小时最多 10 条", limits.Display);
    }

    [Fact]
    public void 上限缺失时不装作有限制()
    {
        Assert.Equal("（桥接没有给出限制）", new VoiceLimits().Display);
    }

    [Theory]
    [InlineData(0, "0 字节")]
    [InlineData(512, "512 字节")]
    [InlineData(2048, "2 KB")]
    [InlineData(32044, "31.3 KB")]
    [InlineData(20971520, "20 MB")]
    [InlineData(2147483648L, "2 GB")]
    public void 字节数换算(long bytes, string expected)
    {
        Assert.Equal(expected, VoiceLimits.FormatBytes(bytes));
    }

    // 桥接实测：POST /api/send/voice 带 dryRun=true（ffprobe 不可用，所以给了警告）
    private const string DryRunJson = """
        {
          "ok": true, "dryRun": true, "key": "group:415520021",
          "file": "D:\\qq-bridge\\audio\\__dlsrc_probe.wav", "via": "voice-dir",
          "bytes": 32044, "duration": null, "codec": "", "detail": "31.3 KB",
          "warnings": ["无法探测时长（ffprobe 不可用：spawn ffprobe ENOENT）：将不做时长校验，直接交给网关"],
          "limits": { "maxSeconds": 300, "maxBytes": 20971520, "maxPerMinute": 2, "maxPerHour": 10 }
        }
        """;

    [Fact]
    public void 试算响应能解析_并且摘要写明没有真的发出去()
    {
        var result = JsonSerializer.Deserialize<VoiceSendResponse>(DryRunJson, Options);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.True(result.DryRun);
        Assert.Equal("group:415520021", result.Key);
        Assert.Equal("voice-dir", result.Via);
        Assert.Equal("✓ 检查通过（没有真的发出去）　·　group:415520021　·　31.3 KB", result.Summary);
    }

    [Fact]
    public void 试算的警告必须出现在正文里()
    {
        var result = JsonSerializer.Deserialize<VoiceSendResponse>(DryRunJson, Options)!;

        // 「降级了但没失败」是最容易被界面吞掉的一类信息：
        // 不显示的话用户会以为时长真的被检查过了，而实际没有。
        Assert.Contains("ffprobe 不可用", result.DetailText);
        Assert.Contains("警告", result.DetailText);
        Assert.Contains(@"D:\qq-bridge\audio\__dlsrc_probe.wav", result.DetailText);
    }

    [Fact]
    public void 桥接拒绝时摘要直接给出它那句话()
    {
        var json = """{ "ok": false, "error": "找不到音频「nope.wav」" }""";

        var result = JsonSerializer.Deserialize<VoiceSendResponse>(json, Options)!;

        Assert.Equal("✗ 失败　找不到音频「nope.wav」", result.Summary);
    }

    [Fact]
    public void 真发成功且落地校验通过时摘要说明到达了会话()
    {
        var json = """
            { "ok": true, "key": "group:415520021", "file": "D:\\qq-bridge\\audio\\a.wav",
              "bytes": 32044, "duration": 1, "codec": "pcm_s16le", "detail": "31.3 KB",
              "warnings": [], "messageId": 12345, "verified": true,
              "audioProcessing": "original", "volume": 1, "loudness": -16 }
            """;

        var result = JsonSerializer.Deserialize<VoiceSendResponse>(json, Options)!;

        Assert.False(result.DryRun);
        Assert.Contains("✓ 已发送", result.Summary);
        Assert.Contains("已确认到达会话", result.Summary);
        Assert.Contains("12345", result.Summary);
    }

    [Fact]
    public void 没能确认到达时不当成成功来报()
    {
        var json = """
            { "ok": true, "key": "group:415520021", "file": "D:\\a.wav", "detail": "31.3 KB",
              "warnings": [], "messageId": 999, "verified": false }
            """;

        var result = JsonSerializer.Deserialize<VoiceSendResponse>(json, Options)!;

        // 发出去和"真的到了"是两回事，这一条就是防着界面上把它写成一件事。
        Assert.Contains("⚠ 没能确认到达会话", result.Summary);
    }

    [Fact]
    public void 消息ID是字符串时也能拼进摘要()
    {
        // 桥接把它透传自 OneBot，不同实现的类型不一定一致。
        var json = """
            { "ok": true, "key": "group:1", "detail": "1 KB", "warnings": [],
              "messageId": "abc-123", "verified": true }
            """;

        var result = JsonSerializer.Deserialize<VoiceSendResponse>(json, Options)!;

        Assert.Contains("abc-123", result.Summary);
    }

    [Fact]
    public void 音频处理细节写进正文()
    {
        var json = """
            { "ok": true, "dryRun": true, "key": "group:1", "file": "D:\\a.wav", "detail": "1 KB",
              "codec": "pcm_s16le", "duration": 3.5, "warnings": [], "limits": null,
              "audioProcessing": "loudnorm", "volume": 0.8, "loudness": -16 }
            """;

        var result = JsonSerializer.Deserialize<VoiceSendResponse>(json, Options)!;

        Assert.Contains("编码：pcm_s16le", result.DetailText);
        Assert.Contains("时长：3.5 秒", result.DetailText);
        Assert.Contains("音频处理：loudnorm　音量 0.8　响度 -16", result.DetailText);
    }

    [Fact]
    public void 转写回读有内容时写进正文()
    {
        var json = """
            { "ok": true, "key": "group:1", "file": "D:\\a.wav", "detail": "1 KB",
              "warnings": [], "readback": "早安" }
            """;

        var result = JsonSerializer.Deserialize<VoiceSendResponse>(json, Options)!;

        Assert.Contains("转写回读：早安", result.DetailText);
    }

    [Fact]
    public void 什么都没有时正文不装出有内容()
    {
        Assert.Equal("（桥接没有给出细节）", new VoiceSendResponse { Ok = true }.DetailText);
    }
}
