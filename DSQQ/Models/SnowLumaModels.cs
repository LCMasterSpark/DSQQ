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

    // 是否已注入。
    [JsonPropertyName("loaded")]
    public bool Loaded { get; set; }

    // 注入状态说明。
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    // 已登录的 QQ 号。
    [JsonPropertyName("uin")]
    public string? Uin { get; set; }
}

// 单个账号的 OneBot 配置（/api/config/:uin）。
public sealed class SnowLumaOneBotConfig
{
    // QQ 号。
    [JsonPropertyName("uin")]
    public string? Uin { get; set; }

    // 网络服务配置。
    [JsonPropertyName("networks")]
    public SnowLumaNetworks? Networks { get; set; }
}

// OneBot 网络服务集合。
public sealed class SnowLumaNetworks
{
    // HTTP API 服务。
    [JsonPropertyName("httpServers")]
    public List<SnowLumaServerEntry> HttpServers { get; set; } = [];

    // WebSocket 服务。
    [JsonPropertyName("wsServers")]
    public List<SnowLumaServerEntry> WsServers { get; set; } = [];
}

// 一个网络服务条目。
public sealed class SnowLumaServerEntry
{
    // 监听主机。
    [JsonPropertyName("host")]
    public string? Host { get; set; }

    // 监听端口。
    [JsonPropertyName("port")]
    public int Port { get; set; }

    // 访问令牌。界面只显示掩码，不完整展示。
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }
}
