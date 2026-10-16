using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSQQ.Models;

// SnowLuma /api/status 的响应（只取界面需要的字段）。
public sealed class SnowLumaStatus
{
    // 运行版本。
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    // 已登录账号列表。
    [JsonPropertyName("accounts")]
    public List<SnowLumaAccount> Accounts { get; set; } = [];
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

// 单个账号的 OneBot 配置（GET/POST /api/config/:uin）。
//
// 用 JsonExtensionData 兜底：POST 是**整体覆盖**语义，凡是我没建模的字段
// 都会在回写时被清掉。把读到的未知字段原样留着，才不会一改配置就丢东西
//（配额那次就是漏了字段，导致 warnMessage 被清空）。
public sealed class SnowLumaOneBotConfig
{
    [JsonPropertyName("uin")]
    public string? Uin { get; set; }

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
