namespace DSQQ.Models;

// 「她是谁」三层（仿真提示词 / 角色卡 / 人设）的一致性审计。
//
// 为什么需要它：这三层都会进入同一条上下文，但没有任何自动校验 ——
//   · 人设的 name 与角色卡正文都能定义"你叫什么"，冲突时谁生效完全看模型怎么权衡；
//   · 角色卡若残留一代指令（[SILENT]、空格分句等），在二代仿真下会与工具协议直接矛盾，
//     其中最坏的一种是模型照抄 [SILENT] 而不调发送工具 —— 结果是"看起来回话了，实际没发出去"。
// 这些都不会报错，只会表现为"她今天怎么怪怪的"，所以值得在界面上主动提示。
//
// 纯逻辑、不依赖 WPF，便于单元测试。
public static class IdentityAuditor
{
    // 一代专用指令的关键词。
    // 与桥接 role-card.js 的 GEN1_ROLE_LINE_RE 同源；那边用它做注入过滤，
    // 这里用它提前提醒用户，避免"改了角色卡、二代行为变怪"这种难查的问题。
    private static readonly string[] Gen1Markers =
    [
        "[SILENT]", "空格分隔", "按空格", "用空格", "空格分句", "空格代表",
        "自动转发", "回复会自动", "输出 [SILENT]",
    ];

    // 人设各字段在桥接侧的长度上限（超出会被静默截断）。
    //
    // ⚠️ 这些值硬编码自 bridge.js 的 cfg.persona 归一化处；桥接的
    // /api/config/persona 目前**不下发** limits，所以没法动态读取。
    // 一旦桥接改了上限，这里会漂移 —— 由单元测试守着一份快照，改了会红。
    public static readonly IReadOnlyDictionary<string, int> PersonaFieldLimits =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["name"] = 40,
            ["tone"] = 200,
            ["style"] = 600,
            ["forbid"] = 600,
            ["extra"] = 600,
        };

    // 人设可达总上限。
    public static int PersonaTotalLimit => PersonaFieldLimits.Values.Sum();

    // 一次审计的输入。
    // 参数 Persona：人设（可为 null 表示未启用）。
    // 参数 PersonaValues：字段 key → 当前文本。
    // 参数 RoleName：当前启用的角色名；未启用为 null。
    // 参数 RoleContent：该角色的正文；未启用为 null。
    // 参数 SimPrompt：仿真提示词正文。
    // 参数 SimPromptPreset：预设名（用于措辞）。
    // 参数 Mode：当前运行模式（chat / reserved2 等）。
    // 参数 SlangChars：黑话表将注入的字符数（近似值）。
    public sealed record Input(
        PersonaConfig? Persona,
        IReadOnlyDictionary<string, string> PersonaValues,
        string? RoleName,
        string? RoleContent,
        string? SimPrompt,
        string? SimPromptPreset,
        string? Mode,
        int SlangChars);

    // 审计结果：警告 + 预算。
    public sealed record Report(IReadOnlyList<string> Warnings, IdentityBudget Budget);

    // 三层各自的字符占用。
    public sealed record IdentityBudget(
        int PersonaChars,
        int PersonaLimit,
        int RoleChars,
        int RoleLimit,
        int SimPromptChars,
        int SlangChars)
    {
        public int TotalChars => PersonaChars + RoleChars + SimPromptChars + SlangChars;
    }

    // 执行审计。
    public static Report Audit(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var warnings = new List<string>();

        var effectivePersona = input.Persona is { Enabled: true } ? input.Persona : null;
        var roleChars = input.RoleContent?.Length ?? 0;

        // ── 1) 人设与角色卡同时定义"叫什么" ──
        // 角色卡正文里出现「你是X/自称X/叫X」这类表述时，与人设的 name 并存会互相打架。
        var personaName = effectivePersona is null ? string.Empty : Value(input, "name").Trim();
        if (personaName.Length > 0 && roleChars > 0 && MentionsSelfName(input.RoleContent!))
        {
            warnings.Add(
                $"人设的「叫什么」填了「{personaName}」，而角色卡正文里也在定义身份 —— "
                + "两处会同时进入上下文，谁生效由模型自行权衡。建议只保留一处，"
                + "或把人设的该项留空。");
        }

        // ── 2) 角色卡残留一代指令（仅二代模式有问题）──
        // chat / reserved（一代）下这些标记是正常用法，不该报警。
        if (roleChars > 0 && IsSecondGeneration(input.Mode))
        {
            var hits = Gen1Markers
                .Where(m => input.RoleContent!.Contains(m, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (hits.Count > 0)
            {
                warnings.Add(
                    $"角色卡含一代专用指令：{string.Join("、", hits)}。"
                    + "二代仿真下文本不会自动发出、必须调工具，这些指令会与协议冲突；"
                    + "其中 [SILENT] 一类最危险 —— 模型可能照抄而不调发送工具，表现为「回话了但没发出去」。"
                    + "建议删除或改用 〔一代〕 小节标记隔开。");
            }
        }

        // ── 3) 人设字段超上限（桥接会静默截断）──
        if (effectivePersona is not null)
        {
            foreach (var (key, limit) in PersonaFieldLimits)
            {
                var len = Value(input, key).Length;
                if (len > limit)
                {
                    warnings.Add($"人设「{key}」已 {len} 字，超过桥接上限 {limit} 字，超出部分会被静默截断。");
                }
            }
        }

        // ── 4) 三层都没启用 —— 她没有任何身份设定 ──
        if (effectivePersona is null && roleChars == 0 && string.IsNullOrWhiteSpace(input.SimPrompt))
        {
            warnings.Add("三层都是空的：她没有任何身份设定，行为会完全依赖仿真提示词的默认协议。");
        }
        else if (effectivePersona is null && roleChars == 0)
        {
            warnings.Add("人设与角色卡都没启用，她只有仿真提示词这一层 —— 语气与身份会比较「默认」。");
        }

        var budget = new IdentityBudget(
            PersonaChars: effectivePersona is null ? 0 : PersonaFieldLimits.Keys.Sum(k => Value(input, k).Length),
            PersonaLimit: PersonaTotalLimit,
            RoleChars: roleChars,
            RoleLimit: 0,   // 由调用方用桥接下发的 limits 覆盖
            SimPromptChars: input.SimPrompt?.Length ?? 0,
            SlangChars: input.SlangChars);

        return new Report(warnings, budget);
    }

    // 是否二代仿真（reserved2）。一代（chat / reserved）下不该对一代指令报警。
    public static bool IsSecondGeneration(string? mode)
        => string.Equals(mode, "reserved2", StringComparison.OrdinalIgnoreCase);

    // 角色卡正文里是否在定义"我是谁 / 我叫什么"。
    // 用宽松匹配：角色卡写法多样（"你是谁"、"自我认知是"、"自称"），
    // 宁可多提示一次，也不要漏掉真实冲突。
    public static bool MentionsSelfName(string roleContent)
    {
        string[] markers = ["你是谁", "自我认知", "自称", "你叫", "你的名字", "名字是"];
        return markers.Any(m => roleContent.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string Value(Input input, string key)
        => input.PersonaValues.TryGetValue(key, out var v) ? v : string.Empty;
}
