using System.Text.Json;
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

    // 角色模式：active（正常）或 silent（静默 —— 不回复群友，管理员私聊照常）。
    // 界面用它初始化静默按钮，避免与桥接实际状态不一致。
    [JsonPropertyName("roleMode")]
    public string? RoleMode { get; set; }

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

// 挂起审批 / 提问（/api/pending）。
// 注意应答机制：挂起项**由 QQ 消息消费** —— 管理员在对应会话里回复"通过/拒绝"即完成审批；
// 提问则回复选项文字或自定义答案。桌面端只做展示与跳转，不去伪造应答。
public sealed class PendingResponse
{
    [JsonPropertyName("pending")]
    public List<PendingItem> Pending { get; set; } = [];
}

// 一个挂起项。
public sealed class PendingItem
{
    // 会话 key（group:群号 或 private:QQ号）。
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    // approval（工具审批）或 question（向管理员提问）。
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    // 仅 approval：待审批的工具名。
    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }

    // 仅 approval：申请理由。
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("approvalId")]
    public string? ApprovalId { get; set; }

    // 仅 question：问题列表。
    [JsonPropertyName("questions")]
    public List<PendingQuestion>? Questions { get; set; }

    public bool IsApproval => string.Equals(Kind, "approval", StringComparison.OrdinalIgnoreCase);

    // 界面显示文本：把"在哪、要你做什么"写全。
    public string Display
    {
        get
        {
            if (IsApproval)
            {
                var tool = string.IsNullOrWhiteSpace(ToolName) ? "(未知工具)" : ToolName;
                var why = string.IsNullOrWhiteSpace(Reason) ? string.Empty : $" —— {Reason}";
                return $"【工具审批】{Key} 请求执行 {tool}{why}";
            }

            var count = Questions?.Count ?? 0;
            var first = Questions?.FirstOrDefault()?.Question;
            return count > 0
                ? $"【提问】{Key}（{count} 个问题）{first}"
                : $"【提问】{Key}";
        }
    }
}

// 一个待回答的问题。
public sealed class PendingQuestion
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("question")]
    public string? Question { get; set; }

    [JsonPropertyName("options")]
    public List<PendingQuestionOption>? Options { get; set; }
}

// 问题的可选项。
public sealed class PendingQuestionOption
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

// 消耗走势（/api/tokens/series）。
public sealed class TokenSeries
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    // 桶宽（毫秒）；默认 30 分钟一桶。
    [JsonPropertyName("bucketMs")]
    public long BucketMs { get; set; }

    [JsonPropertyName("buckets")]
    public List<TokenBucket> Buckets { get; set; } = [];
}

// 一个时间桶。
public sealed class TokenBucket
{
    // 桶起始时间（Unix 毫秒）。
    [JsonPropertyName("t")]
    public long Time { get; set; }

    [JsonPropertyName("cost")]
    public double Cost { get; set; }

    [JsonPropertyName("tokens")]
    public long Tokens { get; set; }

    [JsonPropertyName("turns")]
    public int Turns { get; set; }

    // 计价档位：off（空闲时段）/ peak（高峰）。
    [JsonPropertyName("tier")]
    public string? Tier { get; set; }
}

// 某会话的逐轮花费（/api/tokens/turns?key=…）。
// 响应结构由 tokenLedger.conversation() 决定，这里只取界面要用的部分。
public sealed class TokenTurns
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("totals")]
    public TokenTurnTotals? Totals { get; set; }

    [JsonPropertyName("turns")]
    public List<TokenTurnEntry> Turns { get; set; } = [];
}

public sealed class TokenTurnTotals
{
    [JsonPropertyName("tokens")]
    public long Tokens { get; set; }

    [JsonPropertyName("cost")]
    public double Cost { get; set; }
}

public sealed class TokenTurnEntry
{
    [JsonPropertyName("turn")]
    public int Turn { get; set; }

    [JsonPropertyName("tokens")]
    public long Tokens { get; set; }

    [JsonPropertyName("cost")]
    public double Cost { get; set; }

    [JsonPropertyName("time")]
    public long Time { get; set; }
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

    // 共享会话在 state.sessions 里的注册表键（如 __shared__），不是真实群号。
    // 该模式已重做并可用：注册表键与业务 key 分离，回收会话不会再误删共享映射。
    [JsonPropertyName("ownerKey")]
    public string? OwnerKey { get; set; }
}

// 二代仿真配置（/api/socialV2/config）。
// 只映射当前用得上的开关；桥接那边的配置项远多于此。
public sealed class SocialV2ConfigResponse
{
    [JsonPropertyName("config")]
    public SocialV2Config? Config { get; set; }
}

// 二代仿真配置本体。
public sealed class SocialV2Config
{
    // 是否允许跨群读取：各群上下文仍独立，但她能主动查看其它群的消息。
    [JsonPropertyName("crossGroup")]
    public bool CrossGroup { get; set; }

    // 会话模式（与 /api/config/session-mode 同源）。
    [JsonPropertyName("sessionMode")]
    public string? SessionMode { get; set; }

    // 二代仿真总开关。关掉后她的二代能力整体停用（连工具调用一起停）。
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    // 主动冒泡：没人叫她时也可能自己开口。
    [JsonPropertyName("proactive")]
    public SocialV2Toggle? Proactive { get; set; }

    // 表情包体系（收藏、同步、发送）。
    [JsonPropertyName("sticker")]
    public SocialV2Toggle? Sticker { get; set; }

    // 语音：只管"她能不能发语音"，不影响手动试发。
    [JsonPropertyName("voice")]
    public SocialV2Toggle? Voice { get; set; }
}

// 配置树里带 enabled 的节（proactive / sticker / voice …）。
//
// 只映射 enabled 一个字段：这套配置有十余节、几十个字段，而且会随桥接版本继续长，
// 全量建模的维护成本远高于收益。读取时多余的字段本来就会被忽略，
// 写入时则由 PatchSocialV2ConfigAsync 把整节读回来再交回去，所以都不会丢。
public sealed class SocialV2Toggle
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}


// ── 二代仿真（/api/socialV2/*）──
//
// 这些端点原先只服务于 AI 的工具调用；控制台侧能读到的是"她现在的状态"，
// 排查"她为什么不说话"时要看的就是这一组。

// GET /api/socialV2/states
public sealed class SocialV2StatesResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("conversations")]
    public List<SocialV2Conversation> Conversations { get; set; } = [];
}

// 单个会话的二代状态（state 与 states 里的条目同构）。
public sealed class SocialV2Conversation
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("wakeConfig")]
    public SocialV2WakeConfig? WakeConfig { get; set; }

    // 唤醒保障标志：它们决定"她会不会漏掉消息"。
    [JsonPropertyName("wakeSafety")]
    public SocialV2WakeSafety? WakeSafety { get; set; }

    [JsonPropertyName("unreadCount")]
    public int UnreadCount { get; set; }

    [JsonPropertyName("recentCount")]
    public int RecentCount { get; set; }

    // 上一次被唤醒的原因，例如 "keyword:大肥鱼"。
    [JsonPropertyName("lastWakeReason")]
    public string? LastWakeReason { get; set; }

    [JsonPropertyName("lastAiReplyAt")]
    public long? LastAiReplyAt { get; set; }

    // 列表里显示：会话 + 唤醒模式 + 未读。
    public string Display
    {
        get
        {
            var mode = WakeConfig?.Mode == "active" ? "活跃" : "潜水";
            var unread = UnreadCount > 0 ? $"  未读 {UnreadCount}" : string.Empty;
            return $"{Key}　[{mode}]{unread}";
        }
    }
}

// 唤醒配置。
public sealed class SocialV2WakeConfig
{
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("infinite")]
    public bool Infinite { get; set; }

    [JsonPropertyName("sleepUntil")]
    public string? SleepUntil { get; set; }

    [JsonPropertyName("triggers")]
    public SocialV2Triggers? Triggers { get; set; }

    [JsonPropertyName("batchWindowMs")]
    public long BatchWindowMs { get; set; }

    [JsonPropertyName("lastWakeAt")]
    public long? LastWakeAt { get; set; }

    [JsonPropertyName("wakeCount")]
    public int WakeCount { get; set; }

    // 被唤醒但没做任何动作的次数 —— 偏高说明触发条件太宽。
    [JsonPropertyName("noActionCount")]
    public int NoActionCount { get; set; }

    [JsonPropertyName("confirmedAt")]
    public long? ConfirmedAt { get; set; }

    [JsonPropertyName("confirmedBy")]
    public string? ConfirmedBy { get; set; }

    public string Display
    {
        get
        {
            var mode = Mode == "active" ? "活跃" : "潜水";
            var duration = Infinite ? "无限" : $"有限{(string.IsNullOrWhiteSpace(SleepUntil) ? string.Empty : " 至 " + SleepUntil)}";
            var parts = new List<string> { $"模式：{mode}（{duration}）" };

            if (Triggers is not null)
            {
                parts.Add($"触发：{Triggers.Display}");
            }

            parts.Add($"唤醒 {WakeCount} 次，其中 {NoActionCount} 次没动作");

            if (LastWakeAt is > 0)
            {
                parts.Add($"上次唤醒 {DateTimeOffset.FromUnixTimeMilliseconds(LastWakeAt.Value).ToLocalTime():MM-dd HH:mm}");
            }

            if (!string.IsNullOrWhiteSpace(ConfirmedBy))
            {
                parts.Add($"由 {ConfirmedBy} 设置");
            }

            return string.Join("　", parts);
        }
    }
}

// 唤醒触发条件。
public sealed class SocialV2Triggers
{
    [JsonPropertyName("atMention")]
    public bool AtMention { get; set; }

    [JsonPropertyName("nameMention")]
    public bool NameMention { get; set; }

    [JsonPropertyName("speakerIds")]
    public List<string> SpeakerIds { get; set; } = [];

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = [];

    [JsonPropertyName("question")]
    public bool Question { get; set; }

    [JsonPropertyName("poke")]
    public bool Poke { get; set; }

    [JsonPropertyName("anyMessage")]
    public bool AnyMessage { get; set; }

    [JsonPropertyName("probability")]
    public double Probability { get; set; }

    public string Display
    {
        get
        {
            var parts = new List<string>();

            if (AtMention) { parts.Add("@"); }
            if (NameMention) { parts.Add("名字"); }
            if (Keywords.Count > 0) { parts.Add($"关键词×{Keywords.Count}"); }
            if (Question) { parts.Add("提问"); }
            if (Poke) { parts.Add("拍一拍"); }
            if (SpeakerIds.Count > 0) { parts.Add($"指定成员×{SpeakerIds.Count}"); }
            if (AnyMessage) { parts.Add("任意消息"); }
            if (Probability > 0) { parts.Add($"概率 {Probability:0.##}"); }

            return parts.Count == 0 ? "（无）" : string.Join("/", parts);
        }
    }
}

// 唤醒保障标志。
public sealed class SocialV2WakeSafety
{
    // 硬保障：一定有兜底唤醒。
    [JsonPropertyName("hard")]
    public bool Hard { get; set; }

    // 限时保障。
    [JsonPropertyName("timed")]
    public bool Timed { get; set; }

    [JsonPropertyName("soft")]
    public bool Soft { get; set; }

    // 是否保证能被唤醒。
    [JsonPropertyName("guaranteed")]
    public bool Guaranteed { get; set; }

    // 配置是否已陈旧（长期没更新）。
    [JsonPropertyName("stale")]
    public bool Stale { get; set; }

    public string Display
    {
        get
        {
            var parts = new List<string>();
            if (Hard) { parts.Add("硬保障"); }
            if (Timed) { parts.Add("限时"); }
            if (Soft) { parts.Add("软保障"); }
            if (Guaranteed) { parts.Add("保证可唤醒"); }
            if (Stale) { parts.Add("配置已陈旧"); }
            return parts.Count == 0 ? "（无标志）" : string.Join("　", parts);
        }
    }
}

// GET /api/socialV2/memory
public sealed class SocialV2MemoryResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    // 已渲染成人话的记忆全文 —— 界面直接显示它。
    [JsonPropertyName("formatted")]
    public string? Formatted { get; set; }

    // 结构化原文；结构随记忆类别而变，保留原始 JSON 不做建模。
    [JsonPropertyName("raw")]
    public JsonElement? Raw { get; set; }
}

// GET /api/socialV2/tool-log
public sealed class SocialV2ToolLogResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("entries")]
    public List<SocialV2ToolLogEntry> Entries { get; set; } = [];
}

// 一条工具调用记录。
// 服务端写两种：{type:'call',…,tool,args} 与 {type:'result',…,tool,ok,error}。
public sealed class SocialV2ToolLogEntry
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("tool")]
    public string? Tool { get; set; }

    [JsonPropertyName("args")]
    public JsonElement? Args { get; set; }

    [JsonPropertyName("ok")]
    public bool? Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public string Display
    {
        get
        {
            var time = string.Empty;
            if (DateTimeOffset.TryParse(Time, out var parsed))
            {
                time = parsed.ToLocalTime().ToString("HH:mm:ss");
            }

            var kind = Type == "result" ? (Ok == true ? "结果 ✓" : "结果 ✗") : "调用";
            var detail = Args is { ValueKind: not JsonValueKind.Undefined } args
                ? args.GetRawText()
                : Error ?? string.Empty;

            // 参数可能很长；截断是为了让一行放得下，完整内容仍可从日志文件取。
            if (detail.Length > 160)
            {
                detail = detail[..160] + "…";
            }

            return $"{time} [{kind}] {Tool}　{detail}".TrimEnd();
        }
    }
}

// GET /api/socialV2/feedback
public sealed class SocialV2FeedbackResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("entries")]
    public List<SocialV2FeedbackEntry> Entries { get; set; } = [];
}

// AI 主动上报的一条反馈（桥接 appendFeedbackEntry 写入的形状）。
public sealed class SocialV2FeedbackEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    // info / warning / error
    [JsonPropertyName("level")]
    public string? Level { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    // ISO 8601 时间串。
    [JsonPropertyName("time")]
    public string? Time { get; set; }

    public string Display
    {
        get
        {
            // 级别用符号打头，扫一眼就能挑出 error —— 反馈的价值就在于被看见。
            var mark = Level switch
            {
                "error" => "✗",
                "warning" => "!",
                _ => "·"
            };

            var when = DateTimeOffset.TryParse(Time, out var parsed)
                ? parsed.ToLocalTime().ToString("MM-dd HH:mm")
                : "（时间无法解析）";

            var who = string.IsNullOrWhiteSpace(Key) ? "（无会话）" : Key!.Trim();

            // 反馈正文可能带换行；列表是一行一条，压平才看得清条数。
            // 按 \r\n 一起切分并去掉空段，否则 "\r\n" 会留下两个空格。
            var text = string.IsNullOrWhiteSpace(Message)
                ? "（无内容）"
                : string.Join(' ', Message!.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            return $"{mark} {when}　{who}\n      {text}";
        }
    }
}

// GET /api/socialV2/activity
public sealed class SocialV2ActivityResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    // 二代仿真是否被暂停。
    [JsonPropertyName("paused")]
    public bool Paused { get; set; }
}

// GET /api/stickers —— 管理端表情库（控制台专用，带 agent token 的调用一律 403）。
// 响应外层是 formatStickerList 的结果：total/matched/truncated + stickers。
public sealed class StickerListResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    // 库里的总条目数（不受 query 影响）。
    [JsonPropertyName("total")]
    public int Total { get; set; }

    // 命中 query 的条目数。
    [JsonPropertyName("matched")]
    public int Matched { get; set; }

    // 命中数超过本次请求的 count，列表被截断了。
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("stickers")]
    public List<StickerEntry> Stickers { get; set; } = [];

    [JsonPropertyName("syncedAt")]
    public long SyncedAt { get; set; }

    [JsonPropertyName("fromCache")]
    public bool FromCache { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

// 表情库里的一个条目。id / resId / md5 / url 是四种可用的寻址方式，
// 上游 findStickerEntry 会按 id → resId → md5 → url 顺序匹配。
public sealed class StickerEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("resId")]
    public string? ResId { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    // QQ 侧自带的备注（可读可写，写下去会同步到 QQ 账号）。
    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    // 本地笔记：只存在桥接这边，用来记「这张图是什么意思、什么时候用」。
    [JsonPropertyName("localNote")]
    public string? LocalNote { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("usage")]
    public string? Usage { get; set; }

    [JsonPropertyName("useCount")]
    public int UseCount { get; set; }

    [JsonPropertyName("lastUsedAt")]
    public long LastUsedAt { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    // 保存 / 写回时要用的那个标识：优先 QQ 的表情 id。
    public string MatchId =>
        !string.IsNullOrWhiteSpace(Id) ? Id!
        : !string.IsNullOrWhiteSpace(ResId) ? ResId!
        : !string.IsNullOrWhiteSpace(Md5) ? Md5!
        : Url ?? string.Empty;

    // 列表里显示的一行。备注优先取本地笔记（那是我们自己写给自己看的）。
    public string Display
    {
        get
        {
            var label = !string.IsNullOrWhiteSpace(LocalNote) ? LocalNote!.Trim()
                : !string.IsNullOrWhiteSpace(Desc) ? Desc!.Trim()
                : "（无备注，可先看图）";

            var text = label;

            var tags = Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
            if (tags.Count > 0)
            {
                text += $"　[{string.Join("/", tags)}]";
            }

            if (UseCount > 0)
            {
                text += $"　用过 {UseCount} 次";
            }

            // 用法说明可能很长，只给一个开头，完整内容在右侧编辑框里。
            if (!string.IsNullOrWhiteSpace(Usage))
            {
                var usage = Usage!.Replace("\r", " ").Replace("\n", " ").Trim();
                text += $"　· {usage[..Math.Min(24, usage.Length)]}{(usage.Length > 24 ? "…" : string.Empty)}";
            }

            return text;
        }
    }

    // 右侧编辑区用的标签串（空格分隔，人写起来比逗号顺手）。
    public string TagsText => Tags is { Count: > 0 } ? string.Join(" ", Tags) : string.Empty;
}

// POST /api/stickers/note 与 POST /api/stickers/remark 的响应。
public sealed class StickerSaveResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    // 保存后的条目（桥接会把整条回给调用方）。
    [JsonPropertyName("sticker")]
    public StickerEntry? Sticker { get; set; }

    // 失败原因，例如「找不到表情 xxx」。
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
