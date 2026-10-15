using System.Text.Json.Serialization;

namespace DSQQ.Models;

/// <summary>SnowLuma /api/status 的响应（只取界面需要的字段）。</summary>
public sealed class SnowLumaStatus
{
    /// <summary>运行版本。</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>已登录账号列表。</summary>
    [JsonPropertyName("accounts")]
    public List<SnowLumaAccount> Accounts { get; set; } = [];
}

/// <summary>一个已登录的 QQ 账号。</summary>
public sealed class SnowLumaAccount
{
    /// <summary>QQ 号。</summary>
    [JsonPropertyName("uin")]
    public string? Uin { get; set; }

    /// <summary>昵称。</summary>
    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }
}

/// <summary>QQ 进程与原生注入状态（/api/processes）。</summary>
public sealed class SnowLumaProcess
{
    /// <summary>进程 ID。</summary>
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    /// <summary>进程名。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>是否已注入。</summary>
    [JsonPropertyName("loaded")]
    public bool Loaded { get; set; }

    /// <summary>注入状态说明。</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>已登录的 QQ 号。</summary>
    [JsonPropertyName("uin")]
    public string? Uin { get; set; }
}

/// <summary>单个账号的 OneBot 配置（/api/config/:uin）。</summary>
public sealed class SnowLumaOneBotConfig
{
    /// <summary>QQ 号。</summary>
    [JsonPropertyName("uin")]
    public string? Uin { get; set; }

    /// <summary>网络服务配置。</summary>
    [JsonPropertyName("networks")]
    public SnowLumaNetworks? Networks { get; set; }
}

/// <summary>OneBot 网络服务集合。</summary>
public sealed class SnowLumaNetworks
{
    /// <summary>HTTP API 服务。</summary>
    [JsonPropertyName("httpServers")]
    public List<SnowLumaServerEntry> HttpServers { get; set; } = [];

    /// <summary>WebSocket 服务。</summary>
    [JsonPropertyName("wsServers")]
    public List<SnowLumaServerEntry> WsServers { get; set; } = [];
}

/// <summary>一个网络服务条目。</summary>
public sealed class SnowLumaServerEntry
{
    /// <summary>监听主机。</summary>
    [JsonPropertyName("host")]
    public string? Host { get; set; }

    /// <summary>监听端口。</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; }

    /// <summary>访问令牌。界面只显示掩码，不完整展示。</summary>
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }
}
