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
    //
    // 必须用 FlexibleStringListConverter：桥接把这些数**以数字**返回
    //（"allowGroups": [415520021]），按 List<string> 解析会直接抛
    // "The JSON value could not be converted to System.String"。
    // 而 /api/status 是所有页面共用的入口 —— 一旦解析失败，整个控制台都读不到数据，
    // 表现为"服务未启动"（但实际 TCP 连接是通的）。
    [JsonPropertyName("allowGroups")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string> AllowGroups { get; set; } = [];

    // 允许的私聊号（同上，桥接同样返回数字）。
    [JsonPropertyName("allowPrivate")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
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
// 两个列表都要用 FlexibleStringListConverter：桥接把 QQ 号以**数字**返回
//（"private": [3551510664]），按 List<string> 解析会抛类型错误，
// 导致整条 /api/whitelist 读不到（白名单页整页空白）。
public sealed class WhitelistEntries
{
    // 私聊号码。
    [JsonPropertyName("private")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string> Private { get; set; } = [];

    // 群号。
    [JsonPropertyName("groups")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
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
    // 注意：桥接返回的字段名是 id / name，不是 groupId / groupName。
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

    // 达到上限时自动发出的那句话。
    // 必须映射并原样回传：桥接的保存是整体覆盖，界面若不带上它，
    // 用户在配额页点一次保存就会把这句配置清空。
    [JsonPropertyName("warnMessage")]
    public string WarnMessage { get; set; } = string.Empty;

    // 预警时是否带配图（桥接侧为可空：null = 用默认图）。
    [JsonPropertyName("imageConfigValue")]
    public string? ImageConfigValue { get; set; }

    // 实际生效的配图路径（只读，由桥接计算）。
    [JsonPropertyName("effectiveImage")]
    public string? EffectiveImage { get; set; }

    // 当日群聊已发条数。
    [JsonPropertyName("todayGroup")]
    public int TodayGroup { get; set; }

    // 当日私聊已发条数。
    [JsonPropertyName("todayPrivate")]
    public int TodayPrivate { get; set; }
}

// 桥接 /api/dsh/model-selection 的响应。
// 只反映"当前选中"，不含可选项 —— 可选项在 DshModelCatalog 里。
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

// 桥接 /api/dsh/model 的响应：比 model-selection 多了**可选项与说明**，
// 切换用的下拉框数据来自这里（model-selection 只说明当前选中了什么）。
public sealed class DshModelCatalog
{
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; set; }

    // 可选推理强度（按桥接给定顺序）。
    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];

    // 各推理强度的中文名称与说明。
    [JsonPropertyName("labels")]
    public Dictionary<string, DshEffortLabel> Labels { get; set; } = [];

    // 可选模型。
    [JsonPropertyName("models")]
    public List<DshModelOption> Models { get; set; } = [];
}

// 推理强度的一项说明。
public sealed class DshEffortLabel
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

// 可选模型。
public sealed class DshModelOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    // 下拉框显示文本：模型名 + 标识，方便核对。
    public string Display => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name}（{Id}）";
}

// /api/config/activity 的响应外壳。
// 桥接把配置包在 activity 字段里，和 /api/status 把状态平铺在顶层不一样 ——
// 少解析这一层不会报错，只会让所有字段静默变成 0。
public sealed class ActivityResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("activity")]
    public ActivityConfig? Activity { get; set; }
}

// 活跃度与发送节奏（/api/config/activity）。
// 字段与桥接的 activityState() 一一对应；桥接的 POST 是**部分更新**，
// 只提交改动过的字段即可，未提交的保持现状（因此不会互相覆盖）。
public sealed class ActivityConfig
{
    // ── 潜水与唤醒 —— 决定"被叫到之后多快能回话" ──
    [JsonPropertyName("sleepMinMs")]
    public int SleepMinMs { get; set; }

    // 0 = 不限制最长潜水。
    [JsonPropertyName("sleepMaxMs")]
    public int SleepMaxMs { get; set; }

    // 桥接给出的推荐区间，界面上作参考展示。
    [JsonPropertyName("recommendedSleepMinMs")]
    public int RecommendedSleepMinMs { get; set; }

    [JsonPropertyName("recommendedSleepMaxMs")]
    public int RecommendedSleepMaxMs { get; set; }

    // 每小时最多唤醒次数。
    [JsonPropertyName("maxWakePerHour")]
    public int MaxWakePerHour { get; set; }

    // 每分钟最多唤醒次数（频率上限里最"硬"的一道）。
    [JsonPropertyName("maxWakePerMinute")]
    public int MaxWakePerMinute { get; set; }

    // 睡前静默观察窗口：越短越"敢睡"。
    [JsonPropertyName("preSleepWaitMs")]
    public int PreSleepWaitMs { get; set; }

    // 多条消息合并成一次唤醒的窗口：越短响应越碎、但越快。
    [JsonPropertyName("batchWindowMs")]
    public int BatchWindowMs { get; set; }

    // 多久检查一次"是否可以回消息"。
    [JsonPropertyName("autoReplyCheckMs")]
    public int AutoReplyCheckMs { get; set; }

    // ── 发送节奏与单条长度 ──
    [JsonPropertyName("burstEnabled")]
    public bool BurstEnabled { get; set; }

    // 每小时最多发送条数。
    [JsonPropertyName("maxSendPerHour")]
    public int MaxSendPerHour { get; set; }

    [JsonPropertyName("maxSendPerMinute")]
    public int MaxSendPerMinute { get; set; }

    [JsonPropertyName("burstIntervalMinMs")]
    public int BurstIntervalMinMs { get; set; }

    [JsonPropertyName("burstIntervalMaxMs")]
    public int BurstIntervalMaxMs { get; set; }

    [JsonPropertyName("gapBaseMs")]
    public int GapBaseMs { get; set; }

    [JsonPropertyName("gapPerCharMs")]
    public int GapPerCharMs { get; set; }

    // 单条消息最大字数。
    [JsonPropertyName("maxMessageChars")]
    public int MaxMessageChars { get; set; }
}

// 活跃度预设：一档可整套套用的参数。
// 参数 Name：档位名（高频 / 标准 / 低频）。
// 参数 Description：一句话说明代价与效果，界面在确认框里展示。
// 参数 Values：JSON 键 → 值，键名必须与桥接 /api/config/activity 一致。
public sealed record ActivityPreset(
    string Name,
    string Description,
    IReadOnlyDictionary<string, int> Values);

// 三档预设，由高到低。
//
// 放在模型层而不是视图里，是为了让单元测试能直接校验它们 ——
// 桥接对这个端点有单字段范围校验加四条联动校验，任一不满足就整档 400。
// 取值说明：
//   · sleepMaxMs = 0 表示"不限制最长潜水"，但此时 recommendedSleepMaxMs 不得大于它，
//     所以高频档也不能用 0；
//   · recommendedSleepMinMs <= recommendedSleepMaxMs，且都 >= 1000；
//   · burstIntervalMinMs <= burstIntervalMaxMs。
public static class ActivityPresets
{
    public static IReadOnlyList<ActivityPreset> All { get; } =
    [
        new("高频", "基本随叫随到，token 消耗明显更高", new Dictionary<string, int>
        {
            ["sleepMinMs"] = 10_000,
            ["sleepMaxMs"] = 300_000,
            ["recommendedSleepMinMs"] = 60_000,
            ["recommendedSleepMaxMs"] = 300_000,
            ["maxWakePerHour"] = 60,
            ["maxWakePerMinute"] = 6,
            ["preSleepWaitMs"] = 30_000,
            ["batchWindowMs"] = 2_000,
            ["autoReplyCheckMs"] = 10_000,
            ["maxSendPerHour"] = 200,
            ["maxSendPerMinute"] = 20,
            ["burstIntervalMinMs"] = 500,
            ["burstIntervalMaxMs"] = 2_000,
            ["gapBaseMs"] = 400,
            ["gapPerCharMs"] = 10,
            ["maxMessageChars"] = 500,
        }),
        new("标准", "接近出厂默认，均衡", new Dictionary<string, int>
        {
            ["sleepMinMs"] = 60_000,
            ["sleepMaxMs"] = 1_800_000,
            ["recommendedSleepMinMs"] = 300_000,
            ["recommendedSleepMaxMs"] = 1_800_000,
            ["maxWakePerHour"] = 12,
            ["maxWakePerMinute"] = 1,
            ["preSleepWaitMs"] = 300_000,
            ["batchWindowMs"] = 8_000,
            ["autoReplyCheckMs"] = 30_000,
            ["maxSendPerHour"] = 60,
            ["maxSendPerMinute"] = 8,
            ["burstIntervalMinMs"] = 1_000,
            ["burstIntervalMaxMs"] = 3_000,
            ["gapBaseMs"] = 800,
            ["gapPerCharMs"] = 20,
            ["maxMessageChars"] = 500,
        }),
        new("低频", "很少主动冒泡，省 token", new Dictionary<string, int>
        {
            ["sleepMinMs"] = 300_000,
            ["sleepMaxMs"] = 7_200_000,
            ["recommendedSleepMinMs"] = 900_000,
            ["recommendedSleepMaxMs"] = 7_200_000,
            ["maxWakePerHour"] = 6,
            ["maxWakePerMinute"] = 1,
            ["preSleepWaitMs"] = 300_000,
            ["batchWindowMs"] = 15_000,
            ["autoReplyCheckMs"] = 60_000,
            ["maxSendPerHour"] = 20,
            ["maxSendPerMinute"] = 3,
            ["burstIntervalMinMs"] = 1_500,
            ["burstIntervalMaxMs"] = 5_000,
            ["gapBaseMs"] = 1_200,
            ["gapPerCharMs"] = 30,
            ["maxMessageChars"] = 500,
        }),
    ];
}

// 角色列表（/api/roles）。
public sealed class RoleListResponse
{
    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = [];

    [JsonPropertyName("summaries")]
    public List<RoleSummary> Summaries { get; set; } = [];

    // 当前启用的角色名；未启用为 null。
    [JsonPropertyName("current")]
    public string? Current { get; set; }

    [JsonPropertyName("limits")]
    public RoleLimits? Limits { get; set; }
}

// 单个角色的摘要与校验状态。
public sealed class RoleSummary
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("chars")]
    public int Chars { get; set; }

    // 二代仿真模式下实际注入的字符数。
    [JsonPropertyName("injectedV2")]
    public int InjectedV2 { get; set; }

    // 是否超出注入上限。
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    // 校验告警数（例如含一代专用指令）。
    [JsonPropertyName("warnings")]
    public int Warnings { get; set; }

    [JsonPropertyName("excerpt")]
    public string? Excerpt { get; set; }

    // 列表显示文本：把"多少字/是否超限/有无告警"一次说清，避免用户点进去才发现问题。
    public string Display => $"{Name}（{Chars} 字 / 注入 {InjectedV2} 字"
        + (Truncated ? " · 超上限" : string.Empty)
        + (Warnings > 0 ? $" · {Warnings} 项告警" : string.Empty)
        + "）";
}

// 角色名与正文的长度约束（由桥接下发的 limits）。
public sealed class RoleLimits
{
    [JsonPropertyName("nameMax")]
    public int NameMax { get; set; }

    [JsonPropertyName("contentMaxBytes")]
    public int ContentMaxBytes { get; set; }

    [JsonPropertyName("injectMaxChars")]
    public int InjectMaxChars { get; set; }
}

// 单个角色的正文（/api/roles/content）。
public sealed class RoleContentResponse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

// 仿真提示词（/api/preset/sim-prompt）。
public sealed class SimPromptResponse
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public SimPromptStatus? Status { get; set; }
}

// 仿真提示词的来源信息。
public sealed class SimPromptStatus
{
    [JsonPropertyName("preset")]
    public string? Preset { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("installed")]
    public bool Installed { get; set; }

    // 保存后是否需要重启 DSH 才生效。
    [JsonPropertyName("requiresRestart")]
    public bool RequiresRestart { get; set; }
}

// 黑话词库（/api/slang）。
public sealed class SlangResponse
{
    [JsonPropertyName("entries")]
    public List<SlangEntry> Entries { get; set; } = [];
}

// 一条黑话词条。
public sealed class SlangEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("meaning")]
    public string? Meaning { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    // 只有候选词条才允许"确认/拒绝"操作。
    public bool IsCandidate => string.Equals(Status, "candidate", StringComparison.OrdinalIgnoreCase);

    public string Display => $"[{Status}] {Content}"
        + (string.IsNullOrWhiteSpace(Meaning) ? string.Empty : $" —— {Meaning}")
        + (Count > 0 ? $"（{Count} 次）" : string.Empty);
}

// 会话模式（/api/config/session-mode）。
public sealed class SessionModeResponse
{
    [JsonPropertyName("sessionMode")]
    public string? SessionMode { get; set; }

    [JsonPropertyName("conversationCount")]
    public int ConversationCount { get; set; }

    [JsonPropertyName("sessionCount")]
    public int SessionCount { get; set; }

    // 共享会话模式的所有者 key（该模式当前已停用，仅作展示）。
    [JsonPropertyName("ownerKey")]
    public string? OwnerKey { get; set; }
}
