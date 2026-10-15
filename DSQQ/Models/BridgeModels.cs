using System.Text.Json.Serialization;

namespace DSQQ.Models;

/// <summary>桥接 /api/status 的响应。</summary>
public sealed class BridgeStatus
{
    /// <summary>当前运行模式（chat / closed-agent / reserved / reserved2）。</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>DSH 是否就绪。</summary>
    [JsonPropertyName("dshReady")]
    public bool DshReady { get; set; }

    /// <summary>管理员 QQ 号。</summary>
    [JsonPropertyName("ownerQQ")]
    public long? OwnerQQ { get; set; }

    /// <summary>允许的群号。</summary>
    [JsonPropertyName("allowGroups")]
    public List<string> AllowGroups { get; set; } = [];

    /// <summary>允许的私聊号。</summary>
    [JsonPropertyName("allowPrivate")]
    public List<string> AllowPrivate { get; set; } = [];

    /// <summary>是否已暂停仿真。</summary>
    [JsonPropertyName("socialV2Paused")]
    public bool SocialV2Paused { get; set; }

    /// <summary>SnowLuma 上游健康快照。</summary>
    [JsonPropertyName("snowluma")]
    public BridgeSnowLumaStatus? SnowLuma { get; set; }

    /// <summary>最近活动日志（多行文本）。</summary>
    [JsonPropertyName("activity")]
    public string? Activity { get; set; }
}

/// <summary>桥接视角的 SnowLuma 健康状态。</summary>
/// <remarks>
/// <c>Connected</c> 表示 WebSocket 通；<c>Good</c> 表示"真的收得到 QQ 数据"，两者含义不同。
/// </remarks>
public sealed class BridgeSnowLumaStatus
{
    /// <summary>WebSocket 是否已连接。</summary>
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    /// <summary>QQ 账号是否在线。</summary>
    [JsonPropertyName("accountOnline")]
    public bool? AccountOnline { get; set; }

    /// <summary>上游接收链路是否健康。</summary>
    [JsonPropertyName("good")]
    public bool? Good { get; set; }

    /// <summary>good 的判定来源。</summary>
    [JsonPropertyName("goodSource")]
    public string? GoodSource { get; set; }

    /// <summary>距最近一个事件包的毫秒数。</summary>
    [JsonPropertyName("lastPacketAgoMs")]
    public long? LastPacketAgoMs { get; set; }

    /// <summary>最近事件包类型。</summary>
    [JsonPropertyName("lastPacketKind")]
    public string? LastPacketKind { get; set; }

    /// <summary>是否被判定为退化。</summary>
    [JsonPropertyName("degraded")]
    public bool Degraded { get; set; }

    /// <summary>给用户的处置提示。</summary>
    [JsonPropertyName("hint")]
    public string? Hint { get; set; }
}

/// <summary>桥接 /api/whitelist 的响应。</summary>
public sealed class BridgeWhitelist
{
    /// <summary>允许名单。</summary>
    [JsonPropertyName("allow")]
    public WhitelistEntries Allow { get; set; } = new();

    /// <summary>拒绝名单（优先级高于允许）。</summary>
    [JsonPropertyName("deny")]
    public WhitelistEntries Deny { get; set; } = new();

    /// <summary>管理员 QQ 号。</summary>
    [JsonPropertyName("ownerQQ")]
    public long? OwnerQQ { get; set; }
}

/// <summary>一组白/黑名单。</summary>
public sealed class WhitelistEntries
{
    /// <summary>私聊号码。</summary>
    [JsonPropertyName("private")]
    public List<string> Private { get; set; } = [];

    /// <summary>群号。</summary>
    [JsonPropertyName("groups")]
    public List<string> Groups { get; set; } = [];
}

/// <summary>桥接 /api/qq/groups 的响应。</summary>
public sealed class QqGroupList
{
    /// <summary>机器人在的群。</summary>
    [JsonPropertyName("groups")]
    public List<QqGroup> Groups { get; set; } = [];
}

/// <summary>一个 QQ 群。</summary>
public sealed class QqGroup
{
    /// <summary>群号。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>群名称。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>桥接 /api/sessions 的响应。</summary>
public sealed class BridgeSessions
{
    /// <summary>会话映射列表。</summary>
    [JsonPropertyName("sessions")]
    public List<BridgeSessionEntry> Sessions { get; set; } = [];
}

/// <summary>会话 key 到 DSH 会话 ID 的映射。</summary>
public sealed class BridgeSessionEntry
{
    /// <summary>会话 key（如 group:123 或 private:456）。</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>DSH 会话 ID。</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;
}

/// <summary>桥接 /api/tokens/summary 的响应（只取展示需要的字段）。</summary>
public sealed class TokenSummary
{
    /// <summary>币种符号。</summary>
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    /// <summary>当前计价档位描述。</summary>
    [JsonPropertyName("nowTier")]
    public TokenTier? NowTier { get; set; }

    /// <summary>累计用量。</summary>
    [JsonPropertyName("totals")]
    public TokenTotals? Totals { get; set; }

    /// <summary>折算金额。</summary>
    [JsonPropertyName("cost")]
    public TokenCost? Cost { get; set; }
}

/// <summary>计价档位。</summary>
public sealed class TokenTier
{
    /// <summary>档位标识（off / peak）。</summary>
    [JsonPropertyName("tier")]
    public string? Tier { get; set; }

    /// <summary>档位中文名。</summary>
    [JsonPropertyName("tierLabel")]
    public string? TierLabel { get; set; }

    /// <summary>模型展示名。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

/// <summary>累计用量。</summary>
public sealed class TokenTotals
{
    /// <summary>缓存命中的输入 token。</summary>
    [JsonPropertyName("cacheHit")]
    public long? CacheHit { get; set; }

    /// <summary>缓存未命中的输入 token。</summary>
    [JsonPropertyName("cacheMiss")]
    public long? CacheMiss { get; set; }

    /// <summary>输出 token。</summary>
    [JsonPropertyName("output")]
    public long? Output { get; set; }

    /// <summary>总 token。</summary>
    [JsonPropertyName("total")]
    public long? Total { get; set; }
}

/// <summary>金额折算。</summary>
public sealed class TokenCost
{
    /// <summary>总金额。</summary>
    [JsonPropertyName("total")]
    public decimal? Total { get; set; }
}

/// <summary>桥接 /api/config/persona 的响应。</summary>
public sealed class PersonaConfig
{
    /// <summary>人设是否启用。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>称呼。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>性格基调。</summary>
    [JsonPropertyName("tone")]
    public string Tone { get; set; } = string.Empty;

    /// <summary>说话习惯。</summary>
    [JsonPropertyName("style")]
    public string Style { get; set; } = string.Empty;

    /// <summary>绝对不做的事。</summary>
    [JsonPropertyName("forbid")]
    public string Forbid { get; set; } = string.Empty;

    /// <summary>补充设定。</summary>
    [JsonPropertyName("extra")]
    public string Extra { get; set; } = string.Empty;
}

/// <summary>人设字段的界面标签（由桥接下发，避免本地硬编码与后端漂移）。</summary>
public sealed class PersonaField
{
    /// <summary>字段键。</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>显示标签。</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

/// <summary>人设接口的完整响应。</summary>
public sealed class PersonaResponse
{
    /// <summary>当前人设。</summary>
    [JsonPropertyName("persona")]
    public PersonaConfig Persona { get; set; } = new();

    /// <summary>字段标签。</summary>
    [JsonPropertyName("fields")]
    public List<PersonaField> Fields { get; set; } = [];

    /// <summary>后端附带的说明。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>桥接 /api/config/quota 的响应。</summary>
public sealed class QuotaConfig
{
    /// <summary>总开关。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>群聊每日上限。</summary>
    [JsonPropertyName("groupLimit")]
    public int GroupLimit { get; set; }

    /// <summary>群聊预警点。</summary>
    [JsonPropertyName("groupWarnAt")]
    public int GroupWarnAt { get; set; }

    /// <summary>私聊每日上限。</summary>
    [JsonPropertyName("privateLimit")]
    public int PrivateLimit { get; set; }

    /// <summary>私聊预警点。</summary>
    [JsonPropertyName("privateWarnAt")]
    public int PrivateWarnAt { get; set; }

    /// <summary>豁免号码（不计数、不预警、不拦截）。</summary>
    [JsonPropertyName("exemptOwners")]
    public List<string> ExemptOwners { get; set; } = [];

    /// <summary>预警文案。</summary>
    [JsonPropertyName("warnText")]
    public string WarnText { get; set; } = string.Empty;

    /// <summary>当日群聊已发条数。</summary>
    [JsonPropertyName("todayGroup")]
    public int TodayGroup { get; set; }

    /// <summary>当日私聊已发条数。</summary>
    [JsonPropertyName("todayPrivate")]
    public int TodayPrivate { get; set; }
}

/// <summary>桥接 /api/dsh/model-selection 的响应。</summary>
public sealed class ModelSelection
{
    /// <summary>是否启用模型接管。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>提供方。</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>模型。</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>推理强度。</summary>
    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; set; }

    /// <summary>可选推理强度。</summary>
    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];
}

/// <summary>仿真活跃度配置（/api/config/activity）。</summary>
public sealed class ActivityConfig
{
    /// <summary>最短潜水毫秒数。</summary>
    [JsonPropertyName("sleepMinMs")]
    public int SleepMinMs { get; set; }

    /// <summary>最长潜水毫秒数（0 表示不限制）。</summary>
    [JsonPropertyName("sleepMaxMs")]
    public int SleepMaxMs { get; set; }

    /// <summary>每小时最多唤醒次数。</summary>
    [JsonPropertyName("maxWakePerHour")]
    public int MaxWakePerHour { get; set; }

    /// <summary>每小时最多发送条数。</summary>
    [JsonPropertyName("maxSendPerHour")]
    public int MaxSendPerHour { get; set; }

    /// <summary>单条消息最大字数。</summary>
    [JsonPropertyName("maxMessageChars")]
    public int MaxMessageChars { get; set; }

    /// <summary>每天几点检查可回消息（毫秒）。</summary>
    [JsonPropertyName("autoReplyCheckMs")]
    public int AutoReplyCheckMs { get; set; }
}
