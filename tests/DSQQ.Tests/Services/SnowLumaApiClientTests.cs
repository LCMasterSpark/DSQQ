using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DSQQ.Services;

namespace DSQQ.Tests.Services;

// SnowLuma 客户端的鉴权层。
//
// 这一层原先完全没有测试，而它恰恰藏着最贵的一个 bug：客户端用 cookie 维持会话，
// 但 API 要的是 `Authorization: Bearer <token>`（登录响应里那个 cookie 的
// Path 是 /avatar，只给头像用）。结果是"登录显示成功、之后每个请求都 401"，
// 界面上只表现为"读不到数据"，很难定位。
//
// 所以这里不只测存取，还用**真的 HTTP 往返**验证请求头上到底带了什么 ——
// 只测 Import/Export 是测不出这个 bug 的。
public sealed class SnowLumaApiClientTests
{
    // ── 会话 token 的存取（纯逻辑）──

    [Fact]
    public void 新建客户端应处于未登录状态()
    {
        using var client = new SnowLumaApiClient(new Uri("http://127.0.0.1:1"));

        Assert.False(client.HasSession);
        Assert.Equal(string.Empty, client.ExportToken());
    }

    [Fact]
    public void 导入_token_后应处于已登录状态()
    {
        using var client = new SnowLumaApiClient(new Uri("http://127.0.0.1:1"));

        client.ImportToken("abc123");

        Assert.True(client.HasSession);
        Assert.Equal("abc123", client.ExportToken());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 导入空_token_应视为未登录(string? token)
    {
        using var client = new SnowLumaApiClient(new Uri("http://127.0.0.1:1"));
        client.ImportToken("先有会话");

        client.ImportToken(token);

        Assert.False(client.HasSession);
        Assert.Equal(string.Empty, client.ExportToken());
    }

    [Fact]
    public void 重复导入应以后者为准()
    {
        using var client = new SnowLumaApiClient(new Uri("http://127.0.0.1:1"));

        client.ImportToken("first");
        client.ImportToken("second");

        Assert.Equal("second", client.ExportToken());
    }

    // ── 请求头（真实往返）──

    [Fact]
    public async Task 已登录时请求应带_Bearer_头()
    {
        using var server = new StubServer();
        using var client = new SnowLumaApiClient(server.BaseUri);

        client.ImportToken("tok-abc");
        await client.GetStatusAsync();

        Assert.Equal("Bearer tok-abc", server.LastAuthorization);
    }

    [Fact]
    public async Task 未登录时请求不应带_Authorization_头()
    {
        using var server = new StubServer();
        using var client = new SnowLumaApiClient(server.BaseUri);

        await client.GetStatusAsync();

        // 带上一个空 Authorization 头同样会被服务端当成无效令牌。
        Assert.True(string.IsNullOrEmpty(server.LastAuthorization));
    }

    [Fact]
    public async Task 清除_token_后请求不再带_头()
    {
        using var server = new StubServer();
        using var client = new SnowLumaApiClient(server.BaseUri);

        client.ImportToken("tok-abc");
        await client.GetStatusAsync();
        Assert.Equal("Bearer tok-abc", server.LastAuthorization);

        client.ImportToken(null);
        await client.GetStatusAsync();

        Assert.True(string.IsNullOrEmpty(server.LastAuthorization));
    }

    [Fact]
    public async Task 登录成功应自动记下_token()
    {
        using var server = new StubServer
        {
            ResponseBody = """{"success":true,"token":"from-login"}""",
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var result = await client.LoginAsync("pw");

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        // 这一步是修复的核心：登录拿到 token 后必须留住，后续请求才带得上。
        Assert.True(client.HasSession);
        Assert.Equal("from-login", client.ExportToken());
    }

    [Fact]
    public async Task 登录失败不应留下_token()
    {
        using var server = new StubServer
        {
            StatusCode = 401,
            ResponseBody = """{"success":false,"message":"bad"}""",
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var result = await client.LoginAsync("wrong");

        Assert.Equal(LoginOutcome.BadPassword, result.Outcome);
        Assert.False(client.HasSession);
    }

    [Fact]
    public async Task 登出应清除_token()
    {
        using var server = new StubServer();
        using var client = new SnowLumaApiClient(server.BaseUri);
        client.ImportToken("tok-abc");

        await client.LogoutAsync();

        Assert.False(client.HasSession);
    }

    [Fact]
    public async Task 登录响应缺_token_时不应假装已登录()
    {
        // 只回 success 不给 token（或字段名变了）时，必须留在未登录状态 ——
        // 否则界面会显示"已登录"而所有请求仍然 401。
        using var server = new StubServer
        {
            ResponseBody = """{"success":true}""",
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var result = await client.LoginAsync("pw");

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.False(client.HasSession);
    }

    [Fact]
    public async Task 需要两步验证时应如实报告()
    {
        using var server = new StubServer
        {
            ResponseBody = """{"success":false,"needsTotp":true}""",
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var result = await client.LoginAsync("pw");

        Assert.Equal(LoginOutcome.NeedsTotp, result.Outcome);
        Assert.False(client.HasSession);
    }

    // ── 各端点的响应形状 ──
    //
    // 这几个用例针对一类特别隐蔽的失败：**响应外壳对不上**。
    // 例如 /api/processes 回的是 {"list":[…]}，按裸数组解析会抛 JsonException，
    // 被 SafeDeserialize 吞成 null，再被 `?? []` 兜成空列表 ——
    // 界面上只显示「0 个」，看不出是解析失败还是真的没有。

    [Fact]
    public async Task 进程列表应能解析出外壳里的条目()
    {
        using var server = new StubServer
        {
            Routes =
            {
                ["/api/processes"] = """{"list":[{"pid":11932,"name":"QQ.exe","injected":true,"connected":true,"loggedIn":true,"uin":"3551510664","status":"online","method":"loadModuleManual"}]}""",
            },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var processes = await client.GetProcessesAsync();

        var process = Assert.Single(processes);
        Assert.Equal(11932, process.Pid);
        Assert.True(process.Injected);
        Assert.True(process.LoggedIn);
        Assert.Equal("3551510664", process.Uin);
    }

    [Fact]
    public async Task 空进程列表应回空集合而不是抛错()
    {
        using var server = new StubServer
        {
            Routes = { ["/api/processes"] = """{"list":[]}""" },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        Assert.Empty(await client.GetProcessesAsync());
    }

    [Fact]
    public async Task 运行状态应解析_status_字段()
    {
        // 真实响应就是 {"status":"running"} —— 没有 version / accounts。
        using var server = new StubServer
        {
            Routes = { ["/api/status"] = """{"status":"running"}""" },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var status = await client.GetStatusAsync();

        Assert.NotNull(status);
        Assert.Equal("running", status!.Status);
    }

    [Fact]
    public async Task 账号列表应能解析出外壳里的条目()
    {
        using var server = new StubServer
        {
            Routes = { ["/api/qq-list"] = """{"list":[{"uin":"3551510664","nickname":"O.o"}]}""" },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var accounts = await client.GetQqListAsync();

        var account = Assert.Single(accounts);
        Assert.Equal("3551510664", account.Uin);
        Assert.Equal("O.o", account.Nickname);
    }

    [Fact]
    public async Task 接入点状态应能解析出适配器明细()
    {
        using var server = new StubServer
        {
            Routes =
            {
                ["/api/connections"] = """{"list":[{"uin":"3551510664","nickname":"O.o","adapters":[{"name":"ws-default","kind":"wsServer","status":"ok","detail":"1 个客户端"}]}]}""",
            },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var connections = await client.GetConnectionsAsync();

        var connection = Assert.Single(connections);
        var adapter = Assert.Single(connection.Adapters);
        Assert.Equal("ws-default", adapter.Name);
        Assert.Equal("1 个客户端", adapter.Detail);
    }

    [Fact]
    public async Task 未登录账号的_uin_零应显示为破折号()
    {
        // 未登录时服务端回的是字符串 "0"，直接显示会让人以为真有个 0 号 QQ。
        using var server = new StubServer
        {
            Routes = { ["/api/processes"] = """{"list":[{"pid":524,"name":"QQ.exe","uin":"0"}]}""" },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var processed = await client.GetProcessesAsync();

        Assert.Equal("—", Assert.Single(processed).Account);
    }

    [Fact]
    public async Task OneBot_配置应能穿过_config_包装层()
    {
        // GET /api/config/:uin 回的是 {"config":{…}}。按裸对象解析会让 Networks 恒为 null，
        // 界面于是显示"这个账号还没有任何接入点"—— 而实际上端口都配好了。
        using var server = new StubServer
        {
            Routes =
            {
                ["/api/config/3551510664"] = """
                    {"config":{"networks":{"httpServers":[{"name":"http-default","host":"127.0.0.1","port":3000,"messageFormat":"array"}],"wsServers":[{"name":"ws-default","host":"127.0.0.1","port":3001,"role":"Universal"}]},"statusCommand":{"enabled":true,"trigger":"#sl"}}}
                    """,
            },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var config = await client.GetOneBotConfigAsync("3551510664");

        Assert.NotNull(config);
        Assert.NotNull(config!.Networks);
        Assert.Contains(config.Networks!.HttpServers, s => s.Port == 3000);
        Assert.Contains(config.Networks.WsServers, s => s.Port == 3001);
        Assert.Equal("#sl", config.StatusCommand?.Trigger);
    }

    [Fact]
    public async Task OneBot_配置里未建模的字段应原样保留()
    {
        // POST 是整体覆盖语义。historySync / notifications 这类我没建模的字段
        // 必须靠 JsonExtensionData 留住，否则一改配置就把它们抹掉。
        using var server = new StubServer
        {
            Routes =
            {
                ["/api/config/3551510664"] =
                    """{"config":{"networks":{},"historySync":{"enabled":true},"notifications":{"channelIds":["7"]}}}""",
            },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var config = await client.GetOneBotConfigAsync("3551510664");

        Assert.NotNull(config?.Extra);
        Assert.Contains("historySync", config!.Extra!.Keys);
        Assert.Contains("notifications", config.Extra.Keys);
    }

    [Fact]
    public async Task 保存_OneBot_配置时请求体不应带_config_包装()
    {
        // GET 有包装、POST 不要包装 —— 服务端 POST 直接拿 body 做配置校验。
        // 提交时多包一层会被 assertValidOneBotConfig 拒绝。
        using var server = new StubServer
        {
            ResponseBody = """{"success":true,"saved":true,"applied":true}""",
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var ok = await client.SaveOneBotConfigAsync(
            "3551510664",
            new DSQQ.Models.SnowLumaOneBotConfig
            {
                Networks = new DSQQ.Models.SnowLumaNetworks
                {
                    HttpServers = [new DSQQ.Models.SnowLumaServerEntry { Port = 3000 }],
                },
            });

        Assert.True(ok);

        var body = server.LastRequestBody;
        Assert.NotNull(body);
        Assert.Contains("\"networks\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"config\"", body, StringComparison.Ordinal);
        // 也不该把 uin 塞进配置体：真实配置文件里没有这个字段。
        Assert.DoesNotContain("\"uin\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 日志应能穿过_list_包装层()
    {
        // GET /api/logs 顶层是 {"list":[…]}，又一个外壳。
        using var server = new StubServer
        {
            Routes =
            {
                ["/api/logs"] = """{"list":[{"id":11325,"time":"2026-10-16T11:10:39.516Z","level":"success","scope":"Event","uin":3551510664,"message":"群 [X(415520021)]: [图片]","line":"19:10:39 OK [3551510664] [Event] 群 [X(415520021)]: [图片]"}]}""",
            },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var logs = await client.GetLogsAsync();

        var entry = Assert.Single(logs);
        Assert.Equal("success", entry.Level);
        Assert.Equal("Event", entry.Scope);
        // 展示优先用服务端格式化好的 line。
        Assert.StartsWith("19:10:39 OK", entry.Display, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 日志缺少_line_时应自行拼接展示()
    {
        using var server = new StubServer
        {
            Routes = { ["/api/logs"] = """{"list":[{"time":"T","level":"warn","scope":"Hook","message":"挂载失败"}]}""" },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var entry = Assert.Single(await client.GetLogsAsync());

        Assert.Contains("warn", entry.Display, StringComparison.Ordinal);
        Assert.Contains("挂载失败", entry.Display, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 日志级别应能解析并与可选级别区分()
    {
        using var server = new StubServer
        {
            Routes = { ["/api/logs/level"] = """{"level":"info","levels":["trace","debug","info","success","warn","error"]}""" },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var levels = await client.GetLogLevelAsync();

        Assert.NotNull(levels);
        Assert.Equal("info", levels!.Level);
        Assert.Equal(6, levels.Levels.Count);
        Assert.Contains("trace", levels.Levels);
    }

    [Fact]
    public async Task 设置日志级别应提交_level_字段()
    {
        using var server = new StubServer
        {
            ResponseBody = """{"success":true}""",
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var ok = await client.SetLogLevelAsync("debug");

        Assert.True(ok);
        Assert.Contains("\"level\"", server.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("debug", server.LastRequestBody, StringComparison.Ordinal);
    }

    // ── 系统与存储 ──

    [Fact]
    public async Task 系统信息应解析出机器与负载字段()
    {
        using var server = new StubServer
        {
            Routes =
            {
                ["/api/system"] = """{"hostname":"LAPTOP","platform":"win32","archLabel":"x86_64","release":"10.0.26200","distro":"Windows 11 Pro","uptime":27497.9,"processUptime":26061.2,"nodeVersion":"v22.13.0","cpu":{"model":"Intel","cores":20,"average":17.2},"memory":{"total":34359738368,"used":17179869184,"usagePercent":50}}""",
            },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var info = await client.GetSystemInfoAsync();

        Assert.NotNull(info);
        Assert.Equal("Windows 11 Pro", info!.Distro);
        Assert.Equal(20, info.Cpu?.Cores);
        Assert.Equal(50, info.Memory?.UsagePercent);
    }

    [Fact]
    public async Task 存储信息应区分已保存值与生效值()
    {
        // saved 是用户配置，effective 是算上环境变量后的实际值 ——
        // 两者不一致时要能在界面上提示"有环境变量覆盖"。
        using var server = new StubServer
        {
            Routes =
            {
                ["/api/system/storage"] = """{"settings":{"saved":{"logMaxTotalMb":1024,"logRetainDays":7,"logPerUin":false},"effective":{"logMaxTotalMb":2048,"logRetainDays":7,"logPerUin":false},"envOverrides":["LOG_MAX_TOTAL_MB"]},"snapshot":{"logs":{"state":"healthy","totalBytes":3416553,"fileCount":2,"droppedLines":5},"accounts":[{"uin":"3551510664","nickname":"O.o","online":true,"messagesBytes":6778552,"totalBytes":8228608}],"totals":{"logsBytes":3416553,"accountDataBytes":8228608,"managedBytes":11645161}},"lastCleanup":null}""",
            },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var storage = await client.GetStorageAsync();

        Assert.NotNull(storage);
        Assert.Equal(1024, storage!.Settings?.Saved?.LogMaxTotalMb);
        Assert.Equal(2048, storage.Settings?.Effective?.LogMaxTotalMb);
        // 丢弃行数不为 0 说明日志被截断过，界面要据此提示调大上限。
        Assert.Equal(5, storage.Snapshot?.Logs?.DroppedLines);
        Assert.Equal("O.o", Assert.Single(storage.Snapshot!.Accounts).Nickname);
        Assert.Equal(11645161, storage.Snapshot.Totals?.ManagedBytes);
    }

    [Fact]
    public async Task 保存存储设置应提交三个字段()
    {
        using var server = new StubServer { ResponseBody = """{"success":true}""" };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var ok = await client.SaveStorageSettingsAsync(new DSQQ.Models.SnowLumaStorageSettings
        {
            LogMaxTotalMb = 512,
            LogRetainDays = 3,
            LogPerUin = true,
        });

        Assert.True(ok);

        var body = server.LastRequestBody;
        Assert.Contains("logMaxTotalMb", body, StringComparison.Ordinal);
        Assert.Contains("logRetainDays", body, StringComparison.Ordinal);
        Assert.Contains("logPerUin", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 更新检查应解析出版本与发布页()
    {
        using var server = new StubServer
        {
            Routes =
            {
                ["/api/update/check"] = """{"current":"1.14.22","latest":"1.15.0","hasUpdate":true,"htmlUrl":"https://github.com/SnowLuma/SnowLuma/releases/tag/v1.15.0"}""",
            },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var info = await client.CheckUpdateAsync();

        Assert.NotNull(info);
        Assert.True(info!.HasUpdate);
        Assert.Equal("1.15.0", info.Latest);
        Assert.Contains("github.com", info.HtmlUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 清理存储应把失败如实回报()
    {
        using var server = new StubServer { StatusCode = 500, ResponseBody = """{"success":false}""" };
        using var client = new SnowLumaApiClient(server.BaseUri);

        Assert.False(await client.CleanupStorageAsync());
    }

    // ── 安全与通知 ──

    [Fact]
    public async Task 登录状态应解析出必须改密码标志()
    {
        using var server = new StubServer
        {
            Routes = { ["/api/auth/state"] = """{"mustChangePassword":true}""" },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var state = await client.GetAuthStateAsync();

        Assert.NotNull(state);
        Assert.True(state!.MustChangePassword);
    }

    [Fact]
    public async Task 两步验证状态应解析()
    {
        using var server = new StubServer
        {
            Routes = { ["/api/auth/totp"] = """{"enabled":true}""" },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        Assert.True((await client.GetTotpStateAsync())!.Enabled);
    }

    [Fact]
    public async Task 密码强度应逐条返回并拼成可读行()
    {
        using var server = new StubServer
        {
            Routes =
            {
                ["/api/auth/check-strength"] = """{"rules":[{"id":"length","label":"长度不少于 10 位","ok":false},{"id":"lower","label":"包含小写字母","ok":true}],"valid":false}""",
            },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var result = await client.CheckPasswordStrengthAsync("abc");

        Assert.NotNull(result);
        Assert.False(result!.Valid);
        Assert.Equal(2, result.Rules.Count);
        // 界面直接展示 Display：通过的打勾、没通过的打叉。
        Assert.Contains("✗ 长度不少于 10 位", result.Display, StringComparison.Ordinal);
        Assert.Contains("✓ 包含小写字母", result.Display, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 改密码应提交_oldPassword_与_newPassword()
    {
        // 字段名以服务端实现为准（auth.verify(body.oldPassword) 等）——
        // 写错一个名字会得到"当前密码不正确"，很难看出是字段名的问题。
        using var server = new StubServer { ResponseBody = """{"success":true}""" };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var result = await client.ChangePasswordAsync("old-pw", "NewPw#123456");

        Assert.True(result?.Success);
        Assert.Contains("oldPassword", server.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("newPassword", server.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 确认两步验证应提交_code_与_password()
    {
        using var server = new StubServer { ResponseBody = """{"success":true}""" };
        using var client = new SnowLumaApiClient(server.BaseUri);

        await client.ConfirmTotpAsync("123456", "pw");

        Assert.Contains("\"code\"", server.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"password\"", server.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 关闭两步验证应提交_password_与_totp()
    {
        using var server = new StubServer { ResponseBody = """{"success":true}""" };
        using var client = new SnowLumaApiClient(server.BaseUri);

        await client.DisableTotpAsync("pw", "654321");

        Assert.Contains("\"password\"", server.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"totp\"", server.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 通知配置应穿过_config_包装并保留渠道数()
    {
        using var server = new StubServer
        {
            Routes =
            {
                ["/api/notifications/config"] = """{"config":{"version":1,"debounceSeconds":30,"channels":[{"type":"webhook","url":"https://example.invalid"}]}}""",
            },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var config = await client.GetNotificationsConfigAsync();

        Assert.NotNull(config);
        Assert.Equal(30, config!.DebounceSeconds);
        Assert.Single(config.Channels);
    }

    [Fact]
    public async Task 保存通知配置时必须原样带回渠道()
    {
        // channels 的结构没建模（随渠道类型而变）。POST 是整体覆盖语义，
        // 回写时若把它们丢了，用户的推送渠道会被静默清空。
        using var server = new StubServer
        {
            Routes =
            {
                ["/api/notifications/config"] = """{"config":{"version":1,"debounceSeconds":30,"channels":[{"type":"webhook","url":"https://example.invalid"}]}}""",
            },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var config = await client.GetNotificationsConfigAsync();
        config!.DebounceSeconds = 60;

        // 读与写打同一个路径，所以这里先把桩的响应换成写回执。
        using var writeServer = new StubServer { ResponseBody = """{"success":true}""" };
        using var writeClient = new SnowLumaApiClient(writeServer.BaseUri);
        await writeClient.SaveNotificationsConfigAsync(config);

        var body = writeServer.LastRequestBody;
        Assert.Contains("channels", body, StringComparison.Ordinal);
        Assert.Contains("example.invalid", body, StringComparison.Ordinal);
        Assert.Contains("60", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 最近通知应保留原始文本()
    {
        using var server = new StubServer
        {
            Routes = { ["/api/notifications/recent"] = """{"recent":[{"time":"2026-10-16T11:00:00Z","level":"warn","title":"注入退化"}]}""" },
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var recent = await client.GetRecentNotificationsAsync();

        var item = Assert.Single(recent);
        Assert.Contains("注入退化", item, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 发测试通知应提交_channelId()
    {
        using var server = new StubServer { ResponseBody = """{"success":true}""" };
        using var client = new SnowLumaApiClient(server.BaseUri);

        await client.TestNotificationAsync("chan-1");

        Assert.Contains("channelId", server.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("chan-1", server.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 失败回执里的_message_不应被丢掉()
    {
        // 服务端把失败原因放在 message 里。只看状态码会让用户永远只看到"失败了"。
        using var server = new StubServer
        {
            StatusCode = 401,
            ResponseBody = """{"success":false,"message":"当前密码不正确"}""",
        };
        using var client = new SnowLumaApiClient(server.BaseUri);

        var result = await client.ChangePasswordAsync("wrong", "NewPw#123456");

        Assert.False(result?.Success);
        Assert.Equal("当前密码不正确", result!.Message);
    }

    // ── 极简 HTTP 桩 ──
    //
    // 用 TcpListener 而不是 HttpListener：后者在 Windows 上需要 URL 预留（通常要管理员），
    // 测试会因为权限而失败。TcpListener 绑随机端口没有任何前提。
    private sealed class StubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _loop;

        public StubServer()
        {
            // 端口 0 = 让操作系统分配一个空闲端口，测试之间不会撞车。
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUri = new Uri($"http://127.0.0.1:{port}");

            _loop = Task.Run(AcceptLoopAsync);
        }

        public Uri BaseUri { get; }

        public int StatusCode { get; init; } = 200;

        public string ResponseBody { get; init; } = """{"version":"stub"}""";

        // 按路径定制的响应；未命中的路径回落到 ResponseBody。
        public Dictionary<string, string> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

        // 最近一次请求里 Authorization 头的值；没有该头时为 null。
        public string? LastAuthorization { get; private set; }

        // 最近一次请求的正文（按 Content-Length 读全）；GET 时为 null。
        public string? LastRequestBody { get; private set; }

        private async Task AcceptLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient connection;
                try
                {
                    connection = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                using (connection)
                {
                    await HandleAsync(connection);
                }
            }
        }

        private async Task HandleAsync(TcpClient connection)
        {
            // 每次请求前重置：否则"清除 token 后不再带 Authorization"这类断言
            // 会读到上一次请求留下的值，测试因错误的原因通过。
            LastAuthorization = null;
            LastRequestBody = null;

            var stream = connection.GetStream();
            var buffer = new byte[8192];
            var request = new StringBuilder();

            // 先把头部读完（以空行结束），再按 Content-Length 把正文读全 ——
            // 断言 POST 体形状的用例需要正文，只读头部是不够的。
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0)
                {
                    break;
                }

                request.Append(Encoding.UTF8.GetString(buffer, 0, read));
                headerEnd = request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }

            var text = request.ToString();
            var path = string.Empty;
            var contentLength = 0;

            foreach (var line in text.Split("\r\n"))
            {
                if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    LastAuthorization = line["Authorization:".Length..].Trim();
                }
                else if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
                }
                else if (line.StartsWith("GET ", StringComparison.Ordinal) || line.StartsWith("POST ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 2)
                    {
                        // 去掉查询串，路由按路径匹配。
                        path = parts[1].Split('?')[0];
                    }
                }
            }

            if (headerEnd >= 0 && contentLength > 0)
            {
                var bodySoFar = text[(headerEnd + 4)..];
                var remaining = contentLength - bodySoFar.Length;
                var extra = new StringBuilder(bodySoFar);

                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read <= 0)
                    {
                        break;
                    }

                    extra.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    remaining -= read;
                }

                LastRequestBody = extra.ToString();
            }

            var payload = Routes.TryGetValue(path, out var routed) ? routed : ResponseBody;

            var body = Encoding.UTF8.GetBytes(payload);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {StatusCode} OK\r\n"
                + "Content-Type: application/json\r\n"
                + $"Content-Length: {body.Length}\r\n"
                + "Connection: close\r\n\r\n");

            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _listener.Stop();
            _shutdown.Dispose();

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // 关闭时的取消异常无需处理。
            }
        }
    }
}
