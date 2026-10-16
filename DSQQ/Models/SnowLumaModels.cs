using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSQQ.Models;

// SnowLuma /api/status 的响应。
//
// ⚠️ 实测它的**完整**响应就是 `{"status":"running"}` —— 既没有 version 也没有 accounts。
// 早先这里建模成 {version, accounts}，于是那两个属性永远是空的，
// 概览里一直显示「版本: 未知」「已登录账号: （无）」。账号信息要用 /api/qq-list。
public sealed class SnowLumaStatus
{
    // 运行状态：running。
    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

// /api/processes 的响应外壳。
//
// 顶层是 {"list":[…]} 而不是裸数组 —— 早先按 List<SnowLumaProcess> 直接反序列化，
// 抛 JsonException 后被 SafeDeserialize 吞成 null，于是进程列表**恒为空**，
// 界面显示「QQ 进程 0 个」。包一层外壳才是对的。
public sealed class SnowLumaProcessList
{
    [JsonPropertyName("list")]
    public List<SnowLumaProcess> List { get; set; } = [];
}

// 一个已登录的 QQ 账号。
public sealed class SnowLumaAccount
{
    // QQ 号。
    [JsonPropertyName("uin")]
    public string? Uin { get; set; }

    // 昵称。
    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    // 下拉框里显示的一行。
    public string Display => string.IsNullOrWhiteSpace(Nickname) ? Uin ?? string.Empty : $"{Nickname}（{Uin}）";
}

// QQ 进程与原生注入状态（/api/processes）。
public sealed class SnowLumaProcess
{
    // 进程 ID。
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    // 进程名。
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    // 是否已注入 SnowLuma 的原生模块。
    //
    // ⚠️ 真实字段名是 injected，不是 loaded。早先这里映射成 "loaded"，
    // 那个属性因此永远是 false —— 界面上的注入状态一直是错的，且不报错。
    [JsonPropertyName("injected")]
    public bool Injected { get; set; }

    // 原生链路是否已连通。
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    // QQ 是否已登录。
    [JsonPropertyName("loggedIn")]
    public bool LoggedIn { get; set; }

    // 注入状态说明（available / online 等）。
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    // 已登录的 QQ 号；未登录时是 "0"。
    [JsonPropertyName("uin")]
    public string? Uin { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    // 注入方式，例如 loadModuleManual。
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    // 界面显示用：一句话说清这个进程现在什么状态。
    public string Display
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) ? "(未知进程)" : Name;
            var uin = string.IsNullOrWhiteSpace(Uin) || Uin == "0" ? null : Uin;

            if (!Injected)
            {
                return $"{name} · PID {Pid} · 未加载";
            }

            var state = LoggedIn ? $"已登录 {uin ?? "(未知)"}" : "已加载但未登录";
            return $"{name} · PID {Pid} · {state}";
        }
    }

    // 表格里的账号列。
    // 未登录时 uin 是字符串 "0"，直接显示会让人以为真有个 0 号 QQ。
    public string Account => string.IsNullOrWhiteSpace(Uin) || Uin == "0" ? "—" : Uin;

    // 布尔列用符号显示，比 True/False 好扫。
    public string InjectedText => Injected ? "✓" : "—";

    public string LoggedInText => LoggedIn ? "✓" : "—";
}

// /api/qq-list 的响应：可用的 QQ 账号。
public sealed class SnowLumaQqList
{
    [JsonPropertyName("list")]
    public List<SnowLumaAccount> List { get; set; } = [];
}

// /api/connections 的响应：每个账号的 OneBot 接入点状态。
public sealed class SnowLumaConnections
{
    [JsonPropertyName("list")]
    public List<SnowLumaConnection> List { get; set; } = [];
}

// 一个账号的接入点集合。
public sealed class SnowLumaConnection
{
    [JsonPropertyName("uin")]
    public string? Uin { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("adapters")]
    public List<SnowLumaAdapter> Adapters { get; set; } = [];
}

// 单个接入点（HTTP 或 WebSocket 服务）。
public sealed class SnowLumaAdapter
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    // httpServer / wsServer。
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    // status 为 ok 表示正常。
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    // 人类可读的说明，例如「监听中」「1 个客户端」。
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

// GET /api/config/:uin 的响应外壳。
//
// 实测响应是 {"config":{networks,statusCommand,historySync,notifications}} ——
// **有一层 config 包装**。早先按裸对象直接反序列化，Networks 恒为 null，
// 界面于是显示"这个账号还没有任何接入点"，而实际上 3000/3001 都配好了。
//
// 这是本项目第四次踩同一类坑（活跃度的 {ok,activity}、进程的 {list}、
// 状态的裸 {status}）—— 凡是"读回来是空的"，先怀疑外壳。
public sealed class SnowLumaOneBotConfigResponse
{
    [JsonPropertyName("config")]
    public SnowLumaOneBotConfig? Config { get; set; }
}

// 单个账号的 OneBot 配置。
// GET 时藏在上面那层 config 里；POST 时直接作为请求体（不加包装）。
//
// 用 JsonExtensionData 兜底：POST 是**整体覆盖**语义，凡是我没建模的字段
// 都会在回写时被清掉。把读到的未知字段原样留着，才不会一改配置就丢东西
//（配额那次就是漏了字段，导致 warnMessage 被清空）。
public sealed class SnowLumaOneBotConfig
{
    // 注意：这里**没有** uin 字段。GET 的响应是 {"config":{networks,statusCommand,…}}，
    // 里面的 config 就是配置文件的原文，不含 uin；而 POST 的 body 直接就是这份 config，
    // 服务端会拿 assertValidOneBotConfig(body) 校验 —— 多带未知字段有被拒的风险。
    [JsonPropertyName("networks")]
    public SnowLumaNetworks? Networks { get; set; }

    [JsonPropertyName("statusCommand")]
    public SnowLumaStatusCommand? StatusCommand { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

// OneBot 网络服务集合。
public sealed class SnowLumaNetworks
{
    [JsonPropertyName("httpServers")]
    public List<SnowLumaServerEntry> HttpServers { get; set; } = [];

    [JsonPropertyName("wsServers")]
    public List<SnowLumaServerEntry> WsServers { get; set; } = [];

    [JsonPropertyName("httpClients")]
    public List<SnowLumaServerEntry> HttpClients { get; set; } = [];

    [JsonPropertyName("wsClients")]
    public List<SnowLumaServerEntry> WsClients { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

// 一个网络服务条目。
public sealed class SnowLumaServerEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    // 消息格式：array 或 string。
    [JsonPropertyName("messageFormat")]
    public string? MessageFormat { get; set; }

    [JsonPropertyName("reportSelfMessage")]
    public bool ReportSelfMessage { get; set; }

    // WebSocket 角色（Universal / Api / Event）。
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    // 访问令牌。回环地址下允许为空。
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

// 状态命令（#sl 之类）。
public sealed class SnowLumaStatusCommand
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("swallow")]
    public bool Swallow { get; set; }

    [JsonPropertyName("cooldownSeconds")]
    public int CooldownSeconds { get; set; }

    [JsonPropertyName("trigger")]
    public string? Trigger { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

// /api/logs 的一条日志。
//
// 字段以实测为准：
//   {"id":11325,"time":"2026-10-16T11:10:39.516Z","level":"success","scope":"Event",
//    "uin":3551510664,"message":"群 […] | […]: ID:… [图片]",
//    "line":"19:10:39 OK    [3551510664] [Event] 群 […]"}
//
// line 是服务端**已经格式化好**的展示行（本地时间、级别缩写、uin、scope、正文），
// 直接用它显示比自己拼更省事、也更贴合 SnowLuma 自己的呈现。
public sealed class SnowLumaLogEntry
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    // ISO UTC 时间。
    [JsonPropertyName("time")]
    public string? Time { get; set; }

    // trace / debug / info / success / warn / error。
    [JsonPropertyName("level")]
    public string? Level { get; set; }

    // 来源范围，例如 Event。
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("uin")]
    public long? Uin { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    // 服务端格式化好的整行，供直接展示。
    [JsonPropertyName("line")]
    public string? Line { get; set; }

    // 展示优先用 line；缺失时退化为自己拼。
    public string Display => !string.IsNullOrWhiteSpace(Line)
        ? Line!
        : $"{Time} [{Level}] [{Scope}] {Message}";
}

// /api/logs 的响应外壳（顶层是 {"list":[…]}）。
public sealed class SnowLumaLogList
{
    [JsonPropertyName("list")]
    public List<SnowLumaLogEntry> List { get; set; } = [];
}

// /api/logs/level 的响应：当前级别 + 可选级别。
public sealed class SnowLumaLogLevels
{
    [JsonPropertyName("level")]
    public string? Level { get; set; }

    [JsonPropertyName("levels")]
    public List<string> Levels { get; set; } = [];
}

// ── 系统与存储（/api/system、/api/system/storage）──

// GET /api/system 的系统信息（挑界面用得到的字段）。
public sealed class SnowLumaSystemInfo
{
    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("archLabel")]
    public string? ArchLabel { get; set; }

    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("distro")]
    public string? Distro { get; set; }

    // 主机运行秒数。
    [JsonPropertyName("uptime")]
    public double Uptime { get; set; }

    // SnowLuma 进程运行秒数。
    [JsonPropertyName("processUptime")]
    public double ProcessUptime { get; set; }

    [JsonPropertyName("nodeVersion")]
    public string? NodeVersion { get; set; }

    [JsonPropertyName("cpu")]
    public SnowLumaCpuInfo? Cpu { get; set; }

    [JsonPropertyName("memory")]
    public SnowLumaMemoryInfo? Memory { get; set; }
}

public sealed class SnowLumaCpuInfo
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("cores")]
    public int Cores { get; set; }

    // 平均负载百分比。
    [JsonPropertyName("average")]
    public double Average { get; set; }
}

public sealed class SnowLumaMemoryInfo
{
    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("used")]
    public long Used { get; set; }

    [JsonPropertyName("usagePercent")]
    public double UsagePercent { get; set; }
}

// GET /api/system/storage 的响应。
public sealed class SnowLumaStorageInfo
{
    [JsonPropertyName("settings")]
    public SnowLumaStorageSettingsWrapper? Settings { get; set; }

    [JsonPropertyName("snapshot")]
    public SnowLumaStorageSnapshot? Snapshot { get; set; }

    [JsonPropertyName("lastCleanup")]
    public string? LastCleanup { get; set; }
}

// saved 是用户配置值，effective 是算上环境变量后的实际生效值。
public sealed class SnowLumaStorageSettingsWrapper
{
    [JsonPropertyName("saved")]
    public SnowLumaStorageSettings? Saved { get; set; }

    [JsonPropertyName("effective")]
    public SnowLumaStorageSettings? Effective { get; set; }
}

// POST /api/system/storage/settings 的请求体（也是 GET 里的 saved/effective 形状）。
public sealed class SnowLumaStorageSettings
{
    // 日志总量上限（MB）。
    [JsonPropertyName("logMaxTotalMb")]
    public int LogMaxTotalMb { get; set; }

    // 日志保留天数。
    [JsonPropertyName("logRetainDays")]
    public int LogRetainDays { get; set; }

    // 是否按 QQ 号分文件保留。
    [JsonPropertyName("logPerUin")]
    public bool LogPerUin { get; set; }
}

public sealed class SnowLumaStorageSnapshot
{
    [JsonPropertyName("logs")]
    public SnowLumaLogStorage? Logs { get; set; }

    [JsonPropertyName("accounts")]
    public List<SnowLumaAccountStorage> Accounts { get; set; } = [];

    [JsonPropertyName("totals")]
    public SnowLumaStorageTotals? Totals { get; set; }
}

public sealed class SnowLumaLogStorage
{
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    // 因超限被丢弃的行数 —— 排查"日志怎么断了"时看这个。
    [JsonPropertyName("droppedLines")]
    public long DroppedLines { get; set; }
}

public sealed class SnowLumaAccountStorage
{
    [JsonPropertyName("uin")]
    public string? Uin { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("online")]
    public bool Online { get; set; }

    [JsonPropertyName("messagesBytes")]
    public long MessagesBytes { get; set; }

    [JsonPropertyName("mediaBytes")]
    public long MediaBytes { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }
}

public sealed class SnowLumaStorageTotals
{
    [JsonPropertyName("logsBytes")]
    public long LogsBytes { get; set; }

    [JsonPropertyName("accountDataBytes")]
    public long AccountDataBytes { get; set; }

    [JsonPropertyName("managedBytes")]
    public long ManagedBytes { get; set; }
}

// GET /api/update/check 的响应。
public sealed class SnowLumaUpdateInfo
{
    [JsonPropertyName("current")]
    public string? Current { get; set; }

    [JsonPropertyName("latest")]
    public string? Latest { get; set; }

    [JsonPropertyName("hasUpdate")]
    public bool HasUpdate { get; set; }

    [JsonPropertyName("htmlUrl")]
    public string? HtmlUrl { get; set; }
}


// ── 安全与通知（/api/auth/*、/api/notifications/*）──

// GET /api/auth/state
public sealed class SnowLumaAuthState
{
    // 服务端要求先改密码（例如仍是初始/bootstrap 密码）。
    [JsonPropertyName("mustChangePassword")]
    public bool MustChangePassword { get; set; }
}

// GET /api/auth/totp
public sealed class SnowLumaTotpState
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

// POST /api/auth/check-strength 的一条规则。
public sealed class SnowLumaStrengthRule
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
}

// POST /api/auth/check-strength 的响应。
public sealed class SnowLumaStrengthResult
{
    [JsonPropertyName("rules")]
    public List<SnowLumaStrengthRule> Rules { get; set; } = [];

    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    // 界面直接用：把规则拼成一行勾/叉。
    public string Display => string.Join(
        "  ",
        Rules.Select(r => (r.Ok ? "✓ " : "✗ ") + (r.Label ?? r.Id ?? "?")));
}

// 多数写接口的统一回执：{success, message}。
public sealed class SnowLumaApiResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

// GET /api/notifications/config
public sealed class SnowLumaNotificationsConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    // 同类通知的去抖窗口（秒）。
    [JsonPropertyName("debounceSeconds")]
    public int DebounceSeconds { get; set; }

    // 渠道列表。具体结构随渠道类型而变，界面只数个数、不解释内容 ——
    // 猜错字段名再去回写会直接把配置改坏，不值得。
    [JsonPropertyName("channels")]
    public List<JsonElement> Channels { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

// GET /api/notifications/config 的响应外壳。
public sealed class SnowLumaNotificationsResponse
{
    [JsonPropertyName("config")]
    public SnowLumaNotificationsConfig? Config { get; set; }
}

// GET /api/notifications/recent 的响应外壳。
// 条目结构随渠道而变，因此保留原始 JSON 文本供展示，不做建模。
public sealed class SnowLumaRecentNotifications
{
    [JsonPropertyName("recent")]
    public List<JsonElement> Recent { get; set; } = [];
}
