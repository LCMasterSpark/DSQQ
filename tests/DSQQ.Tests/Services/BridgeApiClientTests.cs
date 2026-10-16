using System.Text.Json;
using DSQQ.Services;
using DSQQ.Tests.Infrastructure;

namespace DSQQ.Tests.Services;

// 二代配置里"只改一处"的那条路径（BridgeApiClient.PatchSocialV2ConfigAsync）。
//
// 单独测它是因为桥接的配置合并是**浅合并**（`{ ...current, ...body }`）：
// 交上去的子对象会整体替换当前值。写错了不会报错，只会静默把用户没碰过的字段
// 清成默认值 —— 这类损失往往过很久才被发现，而且没人会想到是"点了一下开关"造成的。
//
// 所以这里的核心断言不是"改的那个字段变了"，而是"**没改的字段还在**"。
public sealed class BridgeApiClientTests
{
    // 一份带有"兄弟字段"的配置：proactive 和 voice 里除了 enabled 还有别的，
    // 正是那些别的字段最容易被浅合并悄悄清掉。
    private const string ConfigWithSiblings = """
        {"ok":true,"config":{
          "enabled":true,
          "crossGroup":false,
          "sessionMode":"per-conversation",
          "proactive":{"enabled":true,"checkIntervalMinMs":12345,"probability":0.3},
          "sticker":{"enabled":true,"maxListCount":48,"includeInPrompt":true},
          "voice":{"enabled":false,"maxSeconds":60,"ffmpegPath":"C:\\ffmpeg.exe"}
        }}
        """;

    private static StubHttpServer ConfigServer() => new()
    {
        Routes = { ["/api/socialV2/config"] = ConfigWithSiblings },
    };

    [Fact]
    public async Task 改子对象里的一个字段应保留同层其它字段()
    {
        using var server = ConfigServer();
        using var client = new BridgeApiClient(server.BaseUri);

        var ok = await client.PatchSocialV2ConfigAsync("proactive", "enabled", false);

        Assert.True(ok);

        using var body = JsonDocument.Parse(server.LastRequestBody!);
        var proactive = body.RootElement.GetProperty("proactive");

        Assert.False(proactive.GetProperty("enabled").GetBoolean());

        // 这两项没人碰过，必须原样回来 —— 这就是这个方法的全部意义。
        Assert.Equal(12345, proactive.GetProperty("checkIntervalMinMs").GetInt32());
        Assert.Equal(0.3, proactive.GetProperty("probability").GetDouble(), 3);
    }

    [Fact]
    public async Task 提交的正文里不应带上没改的节()
    {
        using var server = ConfigServer();
        using var client = new BridgeApiClient(server.BaseUri);

        await client.PatchSocialV2ConfigAsync("sticker", "enabled", false);

        using var body = JsonDocument.Parse(server.LastRequestBody!);

        // 只交改动的那一节。整份配置回传也能"看起来成功"，
        // 但那会把并发改动（例如用户同时在网页上改的设置）一起覆盖掉。
        Assert.True(body.RootElement.TryGetProperty("sticker", out _));
        Assert.False(body.RootElement.TryGetProperty("voice", out _));
        Assert.False(body.RootElement.TryGetProperty("proactive", out _));
        Assert.False(body.RootElement.TryGetProperty("crossGroup", out _));
        Assert.False(body.RootElement.TryGetProperty("sessionMode", out _));
    }

    [Fact]
    public async Task 改顶层字段时只交这一个字段()
    {
        using var server = ConfigServer();
        using var client = new BridgeApiClient(server.BaseUri);

        // section 传 null 表示顶层。
        var ok = await client.PatchSocialV2ConfigAsync(null, "enabled", false);

        Assert.True(ok);

        using var body = JsonDocument.Parse(server.LastRequestBody!);
        Assert.False(body.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("proactive", out _));
        Assert.False(body.RootElement.TryGetProperty("crossGroup", out _));
    }

    [Fact]
    public async Task 空白节名应与_null_等价都当顶层处理()
    {
        using var server = ConfigServer();
        using var client = new BridgeApiClient(server.BaseUri);

        var ok = await client.PatchSocialV2ConfigAsync("   ", "crossGroup", true);

        Assert.True(ok);

        using var body = JsonDocument.Parse(server.LastRequestBody!);
        Assert.True(body.RootElement.GetProperty("crossGroup").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("proactive", out _));
    }

    [Fact]
    public async Task 要改的节不存在时应失败且不再提交()
    {
        using var server = new StubHttpServer
        {
            Routes = { ["/api/socialV2/config"] = """{"ok":true,"config":{"enabled":true}}""" },
        };
        using var client = new BridgeApiClient(server.BaseUri);

        var ok = await client.PatchSocialV2ConfigAsync("proactive", "enabled", false);

        Assert.False(ok);

        // 只有那次 GET：读不到就绝不提交，否则等于拿一份不完整的配置去覆盖。
        Assert.Single(server.RequestPaths);
        Assert.Contains("proactive", client.LastError);
    }

    [Fact]
    public async Task 读配置失败时不应继续提交()
    {
        using var server = new StubHttpServer { StatusCode = 500, ResponseBody = "{}" };
        using var client = new BridgeApiClient(server.BaseUri);

        var ok = await client.PatchSocialV2ConfigAsync("proactive", "enabled", false);

        Assert.False(ok);
        Assert.Single(server.RequestPaths);
        Assert.Contains("500", client.LastError);
    }

    [Fact]
    public async Task 响应里没有_config_时应失败()
    {
        using var server = new StubHttpServer { ResponseBody = """{"ok":true}""" };
        using var client = new BridgeApiClient(server.BaseUri);

        var ok = await client.PatchSocialV2ConfigAsync("proactive", "enabled", false);

        Assert.False(ok);
        Assert.Single(server.RequestPaths);
        Assert.Contains("config", client.LastError);
    }

    // ── 四个运行开关的读取 ──

    [Fact]
    public async Task 应读出四个运行开关()
    {
        using var server = ConfigServer();
        using var client = new BridgeApiClient(server.BaseUri);

        var config = (await client.GetSocialV2ConfigAsync())?.Config;

        Assert.NotNull(config);
        Assert.True(config!.Enabled);
        Assert.True(config.Proactive?.Enabled);
        Assert.True(config.Sticker?.Enabled);

        // voice 在这份配置里是关的 —— 关着的那项必须能读出来是"关"，
        // 而不是和"没读到"混在一起。
        Assert.False(config.Voice?.Enabled);
    }

    // 缺字段与 false 是两回事：缺 = 从没配置过（桥接默认开），false = 用户真的关过。
    // 界面据此显示"开"，如果把缺当 false 就会显示成"关"，
    // 让人以为设置被谁改过。
    [Fact]
    public async Task 配置里缺字段时应为_null_而不是_false()
    {
        using var server = new StubHttpServer { ResponseBody = """{"config":{"enabled":true}}""" };
        using var client = new BridgeApiClient(server.BaseUri);

        var config = (await client.GetSocialV2ConfigAsync())?.Config;

        Assert.NotNull(config);
        Assert.Null(config!.Proactive);
        Assert.Null(config.Sticker);
        Assert.Null(config.Voice);
        Assert.True(config.Enabled);
    }

    // 整个 config 都缺时不能当成"四个开关都关"。
    [Fact]
    public async Task 没有_config_时配置应为_null()
    {
        using var server = new StubHttpServer { ResponseBody = """{"ok":false,"error":"未启用"}""" };
        using var client = new BridgeApiClient(server.BaseUri);

        Assert.Null((await client.GetSocialV2ConfigAsync())?.Config);
    }

    // 桩必须能读分块编码的请求正文。
    //
    // BridgeApiClient 走 PostAsJsonAsync，它只带 `Transfer-Encoding: chunked`
    // 而没有 Content-Length。桩早先只认 Content-Length，于是这些请求的正文恒为 null，
    // 断言正文的用例会以 "ArgumentNullException: Value cannot be null (json)" 失败 ——
    // 那个报错完全指不到真正的原因。这条用例专门盯住这一点。
    [Fact]
    public async Task 桩应能读到分块编码的请求正文()
    {
        using var server = ConfigServer();
        using var client = new BridgeApiClient(server.BaseUri);

        await client.SaveSocialV2ConfigAsync(new { crossGroup = true });

        Assert.NotNull(server.LastRequestBody);
        Assert.Contains("crossGroup", server.LastRequestBody, StringComparison.Ordinal);
    }

    // ── 唤醒配置写回 ────────────────────────────────────────────────────────
    //
    // POST /api/socialV2/wake-config 的正文是 { key, config } —— config 再嵌一层，
    // 不是平铺。桥接读的是 body.key 与 body.config，写错了会得到「key 不能为空」，
    // 一句和真正原因毫无关系的话。

    [Fact]
    public async Task 唤醒配置的正文应把_config_嵌在_key_旁边()
    {
        using var server = new StubHttpServer { ResponseBody = """{"ok":true}""" };
        using var client = new BridgeApiClient(server.BaseUri);

        await client.SetSocialV2WakeConfigAsync("group:123", new { mode = "diving" });

        using var body = JsonDocument.Parse(server.LastRequestBody!);
        Assert.Equal("group:123", body.RootElement.GetProperty("key").GetString());
        Assert.Equal("diving", body.RootElement.GetProperty("config").GetProperty("mode").GetString());
    }

    // 桥接拒绝时返回 HTTP 400 + {ok:false, error}，而那个 error 是**写给人看的**：
    // 它常常直接说明了要改哪里。换成"保存失败"用户只能去猜。
    [Fact]
    public async Task 桥接拒绝唤醒配置时应转达它的原话()
    {
        using var server = new StubHttpServer
        {
            ResponseBody =
                """{"ok":false,"error":"无限期潜水必须至少保留一个唤醒条件（@/名字/拍一拍/关键词/提问/anyMessage/概率>0），否则 AI 可能永眠"}""",
        };
        using var client = new BridgeApiClient(server.BaseUri);

        var result = await client.SetSocialV2WakeConfigAsync("group:123", new { mode = "diving" });

        // 注意：HTTP 400 也要能把响应体读出来 —— 只按状态码判断就看不到这句话。
        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.Contains("无限期潜水", client.LastError);
    }

    // 被"沉睡前观察窗口"挡住时，桥接会附带还差多少毫秒。界面要能把这段换算成分钟显示。
    [Fact]
    public async Task 被观察窗口挡住时应带回剩余毫秒()
    {
        using var server = new StubHttpServer
        {
            ResponseBody = """{"ok":false,"error":"还不能立刻设置潜水","preSleepWaitRemainingMs":180000}""",
        };
        using var client = new BridgeApiClient(server.BaseUri);

        var result = await client.SetSocialV2WakeConfigAsync("group:123", new { mode = "diving" });

        Assert.Equal(180000, result!.PreSleepWaitRemainingMs);
    }

    // 成功时桥接回传改写后的配置。服务端会动字段（活跃模式强制打开任意消息、
    // 关键词截断、概率夹取），所以界面上要显示的是**回传的这份**，不是刚才提交的。
    [Fact]
    public async Task 保存成功应带回桥接改写后的配置()
    {
        using var server = new StubHttpServer
        {
            ResponseBody = """
                {"ok":true,"key":"group:123","unreadCount":7,
                 "wakeConfig":{"mode":"active","infinite":true,"sleepUntil":null,
                   "triggers":{"atMention":true,"anyMessage":true,"probability":1.5,"keywords":[]},
                   "batchWindowMs":12000,"wakeCount":3,"noActionCount":0},
                 "wakeSafety":{"hard":true,"guaranteed":true}}
                """,
        };
        using var client = new BridgeApiClient(server.BaseUri);

        var result = await client.SetSocialV2WakeConfigAsync("group:123", new { mode = "active" });

        Assert.True(result!.Ok);
        Assert.Equal("active", result.WakeConfig!.Mode);
        Assert.True(result.WakeConfig.Infinite);

        // 桥接把概率夹到了 1.5（不夹的话这里是 0.05 之类的提交值，界面就会显示错的数）。
        Assert.Equal(1.5, result.WakeConfig.Triggers!.Probability, 3);
        Assert.True(result.WakeConfig.Triggers.AnyMessage);
        Assert.True(result.WakeSafety!.Guaranteed);
        Assert.Equal(7, result.UnreadCount);
    }

    // 会话 key 是必填的：没有它桥接不知道该改哪个会话，本方法也不该发请求。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 会话_key_为空时不应发请求(string key)
    {
        using var server = new StubHttpServer { ResponseBody = """{"ok":true}""" };
        using var client = new BridgeApiClient(server.BaseUri);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SetSocialV2WakeConfigAsync(key, new { mode = "diving" }));

        Assert.Null(server.LastRequestBody);
    }

    // ── 语音库 ──

    // 「先检查」与「发送」走的是同一个端点，区别**只**在 dryRun 这一个字段。
    // 桥接里 dryRun 是在限流检查之前就 return 的，所以这个字段一旦没发出去，
    // 用户点「先检查」会真的把语音发出去 —— 这正是本条用例要钉死的东西。
    [Fact]
    public async Task 先检查应把_dryRun_发成_true()
    {
        using var server = new StubHttpServer { ResponseBody = """{"ok":true,"dryRun":true}""" };
        using var client = new BridgeApiClient(server.BaseUri);

        var result = await client.SendVoiceAsync("group:123", "早安.wav", dryRun: true);

        Assert.True(result!.DryRun);
        Assert.Equal("/api/send/voice", server.RequestPaths[^1]);

        using var body = JsonDocument.Parse(server.LastRequestBody!);
        Assert.True(body.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal("group:123", body.RootElement.GetProperty("target").GetString());
        Assert.Equal("早安.wav", body.RootElement.GetProperty("file").GetString());
    }

    [Fact]
    public async Task 发送应把_dryRun_发成_false()
    {
        using var server = new StubHttpServer { ResponseBody = """{"ok":true,"messageId":7}""" };
        using var client = new BridgeApiClient(server.BaseUri);

        await client.SendVoiceAsync("group:123", "早安.wav");

        using var body = JsonDocument.Parse(server.LastRequestBody!);
        Assert.False(body.RootElement.GetProperty("dryRun").GetBoolean());
    }

    // 音量留空表示"用桥接默认"。发个 0 过去会被当成"静音"，那是另一回事。
    [Fact]
    public async Task 音量留空时不发这个字段()
    {
        using var server = new StubHttpServer { ResponseBody = """{"ok":true}""" };
        using var client = new BridgeApiClient(server.BaseUri);

        await client.SendVoiceAsync("group:123", "早安.wav");

        using var body = JsonDocument.Parse(server.LastRequestBody!);
        Assert.False(body.RootElement.TryGetProperty("volume", out _));
    }

    [Fact]
    public async Task 音量有值时应发出去()
    {
        using var server = new StubHttpServer { ResponseBody = """{"ok":true}""" };
        using var client = new BridgeApiClient(server.BaseUri);

        await client.SendVoiceAsync("group:123", "早安.wav", volume: 0.8, normalize: true, dryRun: true);

        using var body = JsonDocument.Parse(server.LastRequestBody!);
        Assert.Equal(0.8, body.RootElement.GetProperty("volume").GetDouble(), 3);
        Assert.True(body.RootElement.GetProperty("normalize").GetBoolean());
    }

    [Fact]
    public async Task 语音列表默认带探测开关()
    {
        using var server = new StubHttpServer { ResponseBody = """{"ok":true,"count":0,"voices":[]}""" };
        using var client = new BridgeApiClient(server.BaseUri);

        await client.GetVoicesAsync();
        Assert.Equal("/api/voice/list?probe=1", server.RequestTargets[^1]);

        await client.GetVoicesAsync(probe: false);
        Assert.Equal("/api/voice/list?probe=0", server.RequestTargets[^1]);
    }

    // 目标与文件都是必填：缺了桥接只能回一句"target 格式应为…"，
    // 倒不如在本地就说清楚，也省一次请求。
    [Theory]
    [InlineData("", "a.wav")]
    [InlineData("   ", "a.wav")]
    [InlineData("group:1", "")]
    [InlineData("group:1", "   ")]
    public async Task 目标或文件为空时不应发请求(string target, string file)
    {
        using var server = new StubHttpServer { ResponseBody = """{"ok":true}""" };
        using var client = new BridgeApiClient(server.BaseUri);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendVoiceAsync(target, file));

        Assert.Null(server.LastRequestBody);
    }
}
