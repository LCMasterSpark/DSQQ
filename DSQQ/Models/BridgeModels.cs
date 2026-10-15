using System.Text.Json.Serialization;

namespace DSQQ.Models;

// 桥接 /api/status 的响应。
public sealed class BridgeStatus
{
    // 当前运行模式（chat / closed-agent / reserved / reserved2）。
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    // DSH 是否就绪。
    [JsonPropertyName("dshReady")]
    public bool DshReady { get; set; }

    // 管理员 QQ 号。
    [JsonPropertyName("ownerQQ")]
    public long? OwnerQQ { get; set; }

    // 允许的群号。
    [JsonPropertyName("allowGroups")]
    public List<string> AllowGroups { get; set; } = [];

    // 允许的私聊号。
    [JsonPropertyName("allowPrivate")]
    public List<string> AllowPrivate { get; set; } = [];

    // 是否已暂停仿真。
    [JsonPropertyName("socialV2Paused")]
    public bool SocialV2Paused { get; set; }

    // SnowLuma 上游健康快照。
    [JsonPropertyName("snowluma")]
    public BridgeSnowLumaStatus? SnowLuma { get; set; }

    // 最近活动日志（多行文本）。
    [JsonPropertyName("activity")]
    public string? Activity { get; set; }
}

// 桥接视角的 SnowLuma 健康状态。
// Connected 表示 WebSocket 通；Good 表示"真的收得到 QQ 数据"，两者含义不同。
public sealed class BridgeSnowLumaStatus
{
    // WebSocket 是否已连接。
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    // QQ 账号是否在线。
    [JsonPropertyName("accountOnline")]
    public bool? AccountOnline { get; set; }

    // 上游接收链路是否健康。
    [JsonPropertyName("good")]
    public bool? Good { get; set; }

    // good 的判定来源。
    [JsonPropertyName("goodSource")]
    public string? GoodSource { get; set; }

    // 距最近一个事件包的毫秒数。
    [JsonPropertyName("lastPacketAgoMs")]
    public long? LastPacketAgoMs { get; set; }

    // 最近事件包类型。
    [JsonPropertyName("lastPacketKind")]
    public string? LastPacketKind { get; set; }

    // 是否被判定为退化。
    [JsonPropertyName("degraded")]
    public bool Degraded { get; set; }

    // 给用户的处置提示。
    [JsonPropertyName("hint")]
    public string? Hint { get; set; }
}

// 桥接 /api/whitelist 的响应。
public sealed class BridgeWhitelist
{
    // 允许名单。
    [JsonPropertyName("allow")]
    public WhitelistEntries Allow { get; set; } = new();

    // 拒绝名单（优先级高于允许）。
    [JsonPropertyName("deny")]
    public WhitelistEntries Deny { get; set; } = new();

    // 管理员 QQ 号。
    [JsonPropertyName("ownerQQ")]
    public long? OwnerQQ { get; set; }
}

// 一组白/黑名单。
public sealed class WhitelistEntries
{
    // 私聊号码。
    [JsonPropertyName("private")]
    public List<string> Private { get; set; } = [];

    // 群号。
    [JsonPropertyName("groups")]
    public List<string> Groups { get; set; } = [];
}

// 桥接 /api/qq/groups 的响应。
public sealed class QqGroupList
{
    // 机器人在的群。
    [JsonPropertyName("groups")]
    public List<QqGroup> Groups { get; set; } = [];
}

// 一个 QQ 群。
public sealed class QqGroup
{
    // 群号。
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    // 群名称。
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

// 桥接 /api/sessions 的响应。
public sealed class BridgeSessions
{
    // 会话映射列表。
    [JsonPropertyName("sessions")]
    public List<BridgeSessionEntry> Sessions { get; set; } = [];
}

// 会话 key 到 DSH 会话 ID 的映射。
public sealed class BridgeSessionEntry
{
    // 会话 key（如 group:123 或 private:456）。
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    // DSH 会话 ID。
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;
}

// 桥接 /api/tokens/summary 的响应（只取展示需要的字段）。
public sealed class TokenSummary
{
    // 币种符号。
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    // 当前计价档位描述。
    [JsonPropertyName("nowTier")]
    public TokenTier? NowTier { get; set; }

    // 累计用量。
    [JsonPropertyName("totals")]
    public TokenTotals? Totals { get; set; }

    // 折算金额。
    [JsonPropertyName("cost")]
    public TokenCost? Cost { get; set; }
}

// 计价档位。
public sealed class TokenTier
{
    // 档位标识（off / peak）。
    [JsonPropertyName("tier")]
    public string? Tier { get; set; }

    // 档位中文名。
    [JsonPropertyName("tierLabel")]
    public string? TierLabel { get; set; }

    // 模型展示名。
    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

// 累计用量。
public sealed class TokenTotals
{
    // 缓存命中的输入 token。
    [JsonPropertyName("cacheHit")]
    public long? CacheHit { get; set; }

    // 缓存未命中的输入 token。
    [JsonPropertyName("cacheMiss")]
    public long? CacheMiss { get; set; }

    // 输出 token。
    [JsonPropertyName("output")]
    public long? Output { get; set; }

    // 总 token。
    [JsonPropertyName("total")]
    public long? Total { get; set; }
}

// 金额折算。
public sealed class TokenCost
{
    // 总金额。
    [JsonPropertyName("total")]
    public decimal? Total { get; set; }
}

// 桥接 /api/config/persona 的响应。
public sealed class PersonaConfig
{
    // 人设是否启用。
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    // 称呼。
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    // 性格基调。
    [JsonPropertyName("tone")]
    public string Tone { get; set; } = string.Empty;

    // 说话习惯。
    [JsonPropertyName("style")]
    public string Style { get; set; } = string.Empty;

    // 绝对不做的事。
    [JsonPropertyName("forbid")]
    public string Forbid { get; set; } = string.Empty;

    // 补充设定。
    [JsonPropertyName("extra")]
    public string Extra { get; set; } = string.Empty;
}

// 人设字段的界面标签（由桥接下发，避免本地硬编码与后端漂移）。
public sealed class PersonaField
{
    // 字段键。
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    // 显示标签。
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

// 人设接口的完整响应。
public sealed class PersonaResponse
{
    // 当前人设。
    [JsonPropertyName("persona")]
    public PersonaConfig Persona { get; set; } = new();

    // 字段标签。
    [JsonPropertyName("fields")]
    public List<PersonaField> Fields { get; set; } = [];

    // 后端附带的说明。
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

// 桥接 /api/config/quota 的响应。
public sealed class QuotaConfig
{
    // 总开关。
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    // 群聊每日上限。
    [JsonPropertyName("groupLimit")]
    public int GroupLimit { get; set; }

    // 群聊预警点。
    [JsonPropertyName("groupWarnAt")]
    public int GroupWarnAt { get; set; }

    // 私聊每日上限。
    [JsonPropertyName("privateLimit")]
    public int PrivateLimit { get; set; }

    // 私聊预警点。
    [JsonPropertyName("privateWarnAt")]
    public int PrivateWarnAt { get; set; }

    // 豁免号码（不计数、不预警、不拦截）。
    [JsonPropertyName("exemptOwners")]
    public List<string> ExemptOwners { get; set; } = [];

    // 预警文案。
    [JsonPropertyName("warnText")]
    public string WarnText { get; set; } = string.Empty;

    // 当日群聊已发条数。
    [JsonPropertyName("todayGroup")]
    public int TodayGroup { get; set; }

    // 当日私聊已发条数。
    [JsonPropertyName("todayPrivate")]
    public int TodayPrivate { get; set; }
}

// 桥接 /api/dsh/model-selection 的响应。
public sealed class ModelSelection
{
    // 是否启用模型接管。
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    // 提供方。
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    // 模型。
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    // 推理强度。
    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; set; }

    // 可选推理强度。
    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];
}

// 仿真活跃度配置（/api/config/activity）。
public sealed class ActivityConfig
{
    // 最短潜水毫秒数。
    [JsonPropertyName("sleepMinMs")]
    public int SleepMinMs { get; set; }

    // 最长潜水毫秒数（0 表示不限制）。
    [JsonPropertyName("sleepMaxMs")]
    public int SleepMaxMs { get; set; }

    // 每小时最多唤醒次数。
    [JsonPropertyName("maxWakePerHour")]
    public int MaxWakePerHour { get; set; }

    // 每小时最多发送条数。
    [JsonPropertyName("maxSendPerHour")]
    public int MaxSendPerHour { get; set; }

    // 单条消息最大字数。
    [JsonPropertyName("maxMessageChars")]
    public int MaxMessageChars { get; set; }

    // 每天几点检查可回消息（毫秒）。
    [JsonPropertyName("autoReplyCheckMs")]
    public int AutoReplyCheckMs { get; set; }
}
