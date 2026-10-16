using System.IO;
using System.Net.Http;
using DSQQ.Models;

namespace DSQQ.Services;

// 一键配置部署：把**能自动做的**都做掉，做不了的明确说出来。
//
// 设计原则：绝不假装成功。QQ 扫码、同意协议、勾选群这些必须由人完成的事，
// 一律返回 NeedsUser 并写清去哪做 —— 一个"全部完成"的假象比失败更浪费时间。
//
// 三层里每一层都有它自己的配置位置：
//   SnowLuma 的 OneBot 端口 → POST /api/config/:uin（整体覆盖语义，必须先读后写）
//   桥接的管理员与白名单   → POST /api/whitelist
//   服务进程本身           → ServiceOrchestrator
public sealed class SetupDeployer
{
    // OneBot 的默认端口。桥接的默认配置就指向它们
    //（config.json 里 wsUrl=ws://127.0.0.1:3001、httpUrl=http://127.0.0.1:3000），
    // 所以这里用固定值；改成别的端口反而要与桥接配置联动，没有必要。
    public const int OneBotHttpPort = 3000;
    public const int OneBotWsPort = 3001;

    private readonly AppConfig _config;
    private readonly TimeSpan _timeout;

    public SetupDeployer(AppConfig config, TimeSpan? timeout = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    // 本次配置过程中拿到的新会话 Cookie（DPAPI 密文）。
    // 调用方应当把它写回配置，下次就不必再登录一次。
    // 为 null 表示本次没有产生新会话。
    public string? RefreshedSessionCookie { get; private set; }

    // 造一个**已登录**的 SnowLuma 客户端。
    //
    // 为什么必须做这一步：SnowLuma 的 /api/qq-list、/api/processes、/api/config
    // 全都要 WebUI 会话，未登录时一律 401。不先登录的话，
    // 「读不到账号」会被误判成「QQ 还没登录」，把用户引到完全错误的方向。
    //
    // 登录顺序：先用配置里存着的会话 Cookie（DPAPI 密文）→ 不行再用密码登一次。
    private async Task<SnowLumaApiClient> CreateSnowLumaClientAsync(CancellationToken cancellationToken)
    {
        var client = new SnowLumaApiClient(
            ServiceEndpoints.BuildLoopbackUri(_config.SnowLuma.PreferredWebUiPort), _timeout);

        // 1) 复用过期的会话。
        var savedCookie = SecureStore.Unprotect(_config.SnowLuma.SessionCookieProtected);
        if (!string.IsNullOrWhiteSpace(savedCookie))
        {
            client.ImportCookies(savedCookie);

            // 用一个必然存在的受保护端点验证会话还有效。
            var probe = await client.GetQqListAsync(cancellationToken).ConfigureAwait(false);
            if (probe.Count > 0)
            {
                return client;
            }
        }

        // 2) 会话失效或没有 → 用存着的密码登录一次。
        var password = SecureStore.Unprotect(_config.SnowLuma.WebUiPasswordProtected);
        if (string.IsNullOrWhiteSpace(password))
        {
            // 交回未登录的客户端：调用方会得到 401，从而给出「请先在 SnowLuma 页登录」的提示。
            return client;
        }

        var login = await client.LoginAsync(password, null, cancellationToken).ConfigureAwait(false);
        if (login.Outcome == LoginOutcome.Success)
        {
            var cookie = client.ExportCookies();
            RefreshedSessionCookie = string.IsNullOrEmpty(cookie) ? null : SecureStore.Protect(cookie);
        }

        return client;
    }

    // 单步结果。
    public enum StepOutcome
    {
        // 已经就绪或本次改好了。
        Done,

        // 本来就不需要动。
        Skipped,

        // 需要用户操作，我们去不了。
        NeedsUser,

        // 出错了。
        Failed,
    }

    public sealed record StepResult(string Name, StepOutcome Outcome, string Message)
    {
        public bool Ok => Outcome is StepOutcome.Done or StepOutcome.Skipped;
    }

    // ── 第 0 步：准备一个 WebUI 密码，让 DSQQ 以后能自动登录 ──

    // 便携版首次运行时生成 WebUI 密码。
    //
    // 为什么需要：SnowLuma 的所有数据接口都要 WebUI 会话。让用户自己去它的界面
    // 设一个密码再回来填，是最容易卡住的一步。改成由 DSQQ 生成并记住 ——
    // 密码经环境变量 SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD 在启动时交给 SnowLuma
    //（AppConfig 里那段注入逻辑已经写好），之后 DSQQ 就能一直自动登录。
    //
    // 必须在 SnowLuma **启动之前**做：它的 WebUI 密码是启动时读环境变量定的。
    //
    // ⚠️ 只对"从未配置过"的 SnowLuma 生成。判断依据是它自己的 config/webui.json
    // 是否存在 —— 那份文件里存着 passwordHash/passwordSalt。若已存在，
    // 说明用户早就设过密码；此时再注入一个会**悄悄把密码换掉**，
    // 用户自己记的那份就失效了，这是不能接受的意外。
    //
    // 返回：本次是否生成了新密码（false 表示已有密码，或不是全新安装）。
    public static bool EnsureBootstrapPassword(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!string.IsNullOrWhiteSpace(config.SnowLuma.WebUiPasswordProtected))
        {
            return false;
        }

        // SnowLuma 已经配置过密码 → 不动它，交给用户在 DSQQ 的 SnowLuma 页填一次。
        var webUiConfig = Path.Combine(
            config.SnowLuma.InstallDirectory ?? string.Empty, "config", "webui.json");

        if (File.Exists(webUiConfig))
        {
            return false;
        }

        var password = GeneratePassword();

        // 两份用途不同，都要留：
        //   WebUiPasswordProtected —— DSQQ 自己以后登录用，DPAPI 加密；
        //   BootstrapPassword      —— 启动 SnowLuma 时经环境变量交给它，必须明文。
        config.SnowLuma.WebUiPasswordProtected = SecureStore.Protect(password);
        config.SnowLuma.BootstrapPassword = password;
        config.SnowLuma.InjectBootstrapPassword = true;

        return true;
    }

    // 生成一个够强的随机密码。
    // 字符集剔除了 0/O/1/I/l 这类易混字符 —— 用户万一要手抄也不会看错。
    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        var chars = new char[bytes.Length];

        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }

    // ── 第 1 步：确保 SnowLuma 的 OneBot 端口开着 ──

    // 参数 uin：要配置的 QQ 号（从 /api/qq-list 取）。
    public async Task<StepResult> EnsureOneBotAsync(string uin, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uin);

        // 必须先登录：这些端点全都要 WebUI 会话，未登录时 401 会被误读成"没配"。
        using var client = await CreateSnowLumaClientAsync(cancellationToken).ConfigureAwait(false);

        var config = await client.GetOneBotConfigAsync(uin, cancellationToken).ConfigureAwait(false);
        if (config is null)
        {
            return new StepResult(
                "OneBot 端口",
                StepOutcome.Failed,
                $"读不到账号 {uin} 的 OneBot 配置（SnowLuma 未登录 WebUI？）");
        }

        // 先看现状：两个端口都齐了就什么都不做，避免无谓地回写配置。
        var changed = EnsureOneBotEndpoints(config);

        if (!changed)
        {
            return new StepResult(
                "OneBot 端口",
                StepOutcome.Skipped,
                $"HTTP {OneBotHttpPort} 与 WS {OneBotWsPort} 都已配置");
        }

        var saved = await client.SaveOneBotConfigAsync(uin, config, cancellationToken).ConfigureAwait(false);
        return saved
            ? new StepResult(
                "OneBot 端口",
                StepOutcome.Done,
                $"已补上 HTTP {OneBotHttpPort} / WS {OneBotWsPort}")
            : new StepResult("OneBot 端口", StepOutcome.Failed, "写入 OneBot 配置失败");
    }

    // 确保配置里有 OneBot 的 HTTP 与 WS 服务；缺的补上。
    //
    // 抽成纯函数是为了能单测：这段一旦写错，用户会得到"启动看起来正常但桥接连不上"的
    // 静默故障，而它完全可以离线验证。
    //
    // 返回：是否发生了改动（false 表示本来就已经配好）。
    public static bool EnsureOneBotEndpoints(SnowLumaOneBotConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var networks = config.Networks ??= new SnowLumaNetworks();

        var hasHttp = networks.HttpServers.Any(s => s.Port == OneBotHttpPort);
        var hasWs = networks.WsServers.Any(s => s.Port == OneBotWsPort);

        if (hasHttp && hasWs)
        {
            return false;
        }

        // 只追加、不重建：用户可能自己加过别的服务（例如另一个端口的 WS）。
        if (!hasHttp)
        {
            networks.HttpServers.Add(new SnowLumaServerEntry
            {
                Name = "http-default",
                Host = "127.0.0.1",
                Port = OneBotHttpPort,
                Path = "/",
                MessageFormat = "array",
                ReportSelfMessage = false,
            });
        }

        if (!hasWs)
        {
            networks.WsServers.Add(new SnowLumaServerEntry
            {
                Name = "ws-default",
                Host = "127.0.0.1",
                Port = OneBotWsPort,
                Path = "/",
                MessageFormat = "array",
                ReportSelfMessage = false,
                // 桥接既收事件又发消息，所以是 Universal 而不是单向角色。
                Role = "Universal",
            });
        }

        return true;
    }

    // ── 第 2 步：把管理员与白名单写进桥接 ──

    // 参数 ownerQQ：管理员 QQ 号；null 表示不改动。
    // 参数 allowGroups / allowPrivate：要启用的会话；传 null 表示不改动。
    public async Task<StepResult> EnsureWhitelistAsync(
        long? ownerQQ,
        IReadOnlyList<string>? allowGroups,
        IReadOnlyList<string>? allowPrivate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new BridgeApiClient(
                ServiceEndpoints.BuildLoopbackUri(_config.Bridge.ConsolePort), _timeout);

            var current = await client.GetWhitelistAsync(cancellationToken).ConfigureAwait(false);
            if (current is null && ownerQQ is null && allowGroups is null)
            {
                return new StepResult(
                    "管理员与白名单",
                    StepOutcome.Failed,
                    $"读不到桥接白名单（控制台端口 {_config.Bridge.ConsolePort} 未监听？）");
            }

            var groups = allowGroups ?? current?.Allow.Groups ?? [];
            var privateList = allowPrivate ?? current?.Allow.Private ?? [];
            var owner = ownerQQ ?? current?.OwnerQQ;

            var saved = await client
                .SaveWhitelistAsync(privateList, groups, owner, cancellationToken)
                .ConfigureAwait(false);

            if (!saved)
            {
                return new StepResult("管理员与白名单", StepOutcome.Failed, "桥接拒绝了白名单写入");
            }

            var parts = new List<string>();
            if (ownerQQ is not null)
            {
                parts.Add($"管理员 {ownerQQ}");
            }

            parts.Add($"{groups.Count} 个群 / {privateList.Count} 个私聊");

            return new StepResult("管理员与白名单", StepOutcome.Done, string.Join(" · ", parts));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new StepResult("管理员与白名单", StepOutcome.Failed, $"无法连接桥接：{ex.GetType().Name}");
        }
    }

    // ── 第 3 步：取当前登录的 QQ 号（后面两步都要用）──

    // 返回：登录中的 QQ 号；没有则为 null。
    public async Task<string?> GetLoggedInUinAsync(CancellationToken cancellationToken = default)
    {
        // 必须先登录 WebUI，否则这些端点一律 401 —— 那会被误读成"QQ 没登录"。
        using var client = await CreateSnowLumaClientAsync(cancellationToken).ConfigureAwait(false);

        // 优先用 /api/qq-list —— 它只在真的登录成功后才有内容。
        var accounts = await client.GetQqListAsync(cancellationToken).ConfigureAwait(false);
        var fromList = accounts.FirstOrDefault()?.Uin;
        if (!string.IsNullOrWhiteSpace(fromList))
        {
            return fromList;
        }

        // 退化到进程列表：loggedIn 为真的那条。
        var processes = await client.GetProcessesAsync(cancellationToken).ConfigureAwait(false);
        return processes.FirstOrDefault(p => p.LoggedIn && p.Uin is not null and not "0")?.Uin;
    }

    // 通过一次"能自动化的都做掉"的批量配置。
    //
    // 参数 ownerQQOverride：管理员 QQ；null 表示用当前登录的 QQ 号预填。
    // 返回每一步的结果，供界面逐条显示。
    public async Task<IReadOnlyList<StepResult>> RunAsync(
        long? ownerQQOverride = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<StepResult>();

        // 1) 找到登录的 QQ 号 —— 后面 OneBot 配置与管理员预填都依赖它。
        var uin = await GetLoggedInUinAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uin))
        {
            results.Add(new StepResult(
                "QQ 登录",
                StepOutcome.NeedsUser,
                await BuildLoginHintAsync(cancellationToken).ConfigureAwait(false)));
            return results;
        }

        results.Add(new StepResult("QQ 登录", StepOutcome.Done, $"已登录 {uin}"));

        // 2) OneBot 端口。
        results.Add(await EnsureOneBotAsync(uin, cancellationToken).ConfigureAwait(false));

        // 3) 管理员与白名单。
        //    管理员默认预填成登录的 QQ 号 —— 自用场景里机器人号就是管理员号，
        //    省掉用户手填；有第二个号的用户可以之后自己改。
        var owner = ownerQQOverride
            ?? (long.TryParse(uin, out var parsed) ? parsed : null);

        results.Add(await EnsureWhitelistAsync(owner, null, null, cancellationToken).ConfigureAwait(false));

        return results;
    }

    // 拼出"读不到 QQ 账号"时该做什么。
    //
    // 这一步的措辞值得较真：读不到账号有**三种**成因，指向的动作完全不同 ——
    // SnowLuma 没启动、DSQQ 没登录它的 WebUI、以及 QQ 本身没扫码。
    // 一律说"请去扫码"会把用户引到错的地方。
    private async Task<string> BuildLoginHintAsync(CancellationToken cancellationToken)
    {
        var webUiOpen = await PortProbe
            .IsPortOpenAsync(_config.SnowLuma.PreferredWebUiPort, cancellationToken)
            .ConfigureAwait(false);

        if (!webUiOpen)
        {
            return $"SnowLuma 没在运行（WebUI 端口 {_config.SnowLuma.PreferredWebUiPort} 未监听）。"
                + "到「服务管理」页点一键启动，然后回来点「重新检查」。";
        }

        // 已经跑起来了却读不到 → 先看 DSQQ 自己有没有这个 WebUI 的会话。
        var hasSession = !string.IsNullOrWhiteSpace(_config.SnowLuma.SessionCookieProtected);
        var hasPassword = !string.IsNullOrWhiteSpace(_config.SnowLuma.WebUiPasswordProtected);

        if (!hasSession && !hasPassword)
        {
            return "SnowLuma 在运行，但 DSQQ 还没有它的 WebUI 会话。"
                + "到「SnowLuma」页用它的 WebUI 密码登录一次（DSQQ 会记住），"
                + "然后在那边同意用户协议并用 QQ 扫码登录。完成后回来点「重新检查」。";
        }

        return "SnowLuma 在运行，但还没有登录中的 QQ 账号。"
            + "点「服务管理」页的「打开 SnowLuma 控制台」，在那边同意用户协议并用 QQ 扫码登录"
            + "（只需一次），完成后回来点「重新检查」。";
    }
}
