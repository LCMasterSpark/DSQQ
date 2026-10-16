using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DSQQ.Models;
using DSQQ.Services;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Button = System.Windows.Controls.Button;

namespace DSQQ.Views;

// 桥接控制台页：模式、白名单、人设、配额、令牌与花费。
// 全部通过桥接的 HTTP API 完成，不内嵌网页。
// 保存类操作即时生效；改变端口等配置项需要重启桥接（页面上另有提示）。
public partial class BridgeConsoleView : UserControl
{
    // 可选运行模式（与桥接 /api/mode 接受的值一致）。
    private static readonly (string Value, string Label)[] Modes =
    [
        ("chat", "聊天（白名单群 + 私聊）"),
        ("closed-agent", "封闭 Agent（仅管理员私聊）"),
        ("reserved", "一代仿真"),
        ("reserved2", "二代仿真"),
    ];

    private readonly AppServices _services;
    private readonly ObservableCollection<GroupItem> _groups = [];
    private readonly ObservableCollection<BridgeSessionEntry> _sessions = [];
    private BridgeApiClient _client;

    // 最近一次会话快照（供状态应用时复用，避免重复请求）。
    private IReadOnlyList<BridgeSessionEntry> _sessionsSnapshot = [];

    // 上次载入的活跃度配置。保存时以它为基准做比对，只提交真正改动过的字段，
    // 避免把用户没碰过的项一起写回（桥接是部分更新语义，未提交的保持现状）。
    private ActivityConfig? _activityBaseline;

    // 上次载入的配额。用于在保存前提示"上限提示语会被清空"这类整体覆盖导致的数据丢失。
    private QuotaConfig? _quotaBaseline;

    // 上次载入的模型与强度，用于判断是否真的改过（未改就不发请求）。
    private ModelSelection? _modelBaseline;

    // 当前是否处于静默模式（由按钮切换并本地记住，避免每次都要问桥接）。
    private bool _roleModeIsSilent;

    // 当前运行模式（chat / reserved2 …）。审计要用它判断"一代指令"是否算冲突。
    private string? _currentMode;

    // 桥接下发的角色卡注入上限；0 表示还没取到，界面上退化为 6000。
    private int _roleInjectLimit;

    // 初始化页面。
    // 参数 services：应用服务宿主。
    // 异常 ArgumentNullException：为 null。
    public BridgeConsoleView(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _client = new BridgeApiClient(_services.Endpoints.BridgeConsole);

        InitializeComponent();

        ModeCombo.ItemsSource = Modes.Select(m => m.Label).ToList();
        GroupList.ItemsSource = _groups;
        RoleList.ItemsSource = _roles;
        SlangList.ItemsSource = _slang;

        SlangFilterCombo.ItemsSource = new[] { "全部", "仅候选", "仅已确认" };
        SlangFilterCombo.SelectedIndex = 0;

        PresetCombo.ItemsSource = Presets;
        PresetCombo.SelectedIndex = Presets.Length - 1;
        SessionGrid.ItemsSource = _sessions;

        // 索引 0 是潜水、1 是活跃（FillWakeEditor 与保存都按这个顺序取值）。
        WakeModeCombo.ItemsSource = new[] { "潜水", "活跃" };
        WakeModeCombo.SelectedIndex = 0;
        WakeModeCombo.SelectionChanged += OnWakeModeChanged;

        Loaded += async (_, _) => await ReloadAsync();
    }

    // 群列表项。
    // 参数 Id：群号。
    // 参数 Name：群名。
    //
    // 白名单页的 ListBox 是 DisplayMemberPath="Display"，而 _groups 里存放的就是本类型，
    // 所以显示文本必须由**这里**提供 —— 放到数据模型 QqGroup 上不会生效，
    // 而绑定失败时 WPF 不报错，只把每一项渲染成空白行。
    public sealed record GroupItem(string Id, string Name)
    {
        // 界面显示文本：群名 + 群号（群号用于核对，同名群也能区分）。
        public string Display => $"{Name}（{Id}）";
    }

    // 重新载入全部数据。
    private async void OnReloadClick(object sender, RoutedEventArgs e) => await ReloadAsync();

    // 读取桥接状态，失败时短间隔重试几次。
    //
    // 为什么需要：DSQQ 与 SnowLuma / 桥接常被一起启动，桥接要等端口就绪才可访问，
    // 而本页在窗口 Loaded 时就会拉数据。若一次失败就放弃，界面会停在"服务未启动"，
    // 且所有下拉框保持为空 —— 用户看到的是"下拉展不开"，其实只是没数据。
    // 总耗时不长（约 6 秒），成功即返回，不会拖慢正常路径。
    private async Task<Models.BridgeStatus?> GetStatusWithRetryAsync()
    {
        const int Attempts = 4;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var status = await _client.GetStatusAsync();
            if (status is not null)
            {
                return status;
            }

            if (attempt == Attempts)
            {
                break;
            }

            // 把具体原因显示出来：长期只显示"服务未启动"会把超时、404、解析失败
            // 这类完全不同的故障混为一谈，排查时无从下手。
            StatusText.Text = $"桥接读取失败（第 {attempt} 次）：{_client.LastError ?? "未知原因"}，正在重试…";
            await Task.Delay(TimeSpan.FromSeconds(1 + attempt));
        }

        return null;
    }

    // 拉取桥接的各类配置与状态。
    private async Task ReloadAsync()
    {
        // 配置可能在设置页被改过（端口变化），因此每次重新载入都重建客户端。
        _client.Dispose();
        _client = new BridgeApiClient(_services.Endpoints.BridgeConsole);

        // 每次重载前清空旧数据，避免叠加。
        StatusText.Text = "正在载入…";

        // 首次探测带重试：DSQQ 常与 SnowLuma / 桥接一起启动，桥接要等端口就绪，
        // 而这一页在窗口 Loaded 时就会拉取。一次失败就放弃的话，界面会停在
        // "服务未启动"并把所有下拉框留空（表现为"下拉展不开"），而实际上桥接稍后就绪。
        var status = await GetStatusWithRetryAsync();
        if (status is null)
        {
            StatusText.Text = $"无法读取桥接（{_services.Endpoints.BridgeConsole}）：{_client.LastError ?? "服务未启动或端口不对"}";
            HintText.Text = "提示：可在「服务管理」页启动桥接，或在「设置」页修正端口；桥接就绪后点「重新载入」即可。";
            return;
        }

        ApplyStatus(status);

        // 状态里带着 roleMode，用它初始化静默按钮，避免与桥接实际状态不一致。
        _roleModeIsSilent = string.Equals(status.RoleMode, "silent", StringComparison.OrdinalIgnoreCase);
        UpdateSilentUi();

        await LoadWhitelistAsync();
        await LoadGroupsAsync();
        await LoadPersonaAsync();
        await LoadQuotaAsync();
        await LoadActivityAsync();
        await LoadSessionContextAsync();
        await LoadTokensAsync();
        await LoadPendingAsync();
        await LoadCostTrendAsync();
        await LoadRolesAsync();
        await LoadSlangAsync();
        await LoadSocialV2Async();

        UpdateIdentityAudit();

        StatusText.Text = $"已载入（{DateTime.Now:HH:mm:ss}）";
        HintText.Text = $"桥接地址：{_services.Endpoints.BridgeConsole}";
    }

    // 应用运行状态：模式、会话、模型。
    private void ApplyStatus(BridgeStatus status)
    {
        var index = Array.FindIndex(Modes, m => m.Value == status.Mode);
        ModeCombo.SelectedIndex = index >= 0 ? index : 0;

        _sessions.Clear();
        foreach (var session in _sessionsSnapshot)
        {
            _sessions.Add(session);
        }

        var snowLuma = status.SnowLuma;
        ModelText.Text = $"DSH 就绪 {status.DshReady} / SnowLuma {SnowLumaHealthText.Describe(snowLuma)}";
    }

    // 载入白名单。
    private async Task LoadWhitelistAsync()
    {
        var whitelist = await _client.GetWhitelistAsync();
        if (whitelist is null)
        {
            return;
        }

        AllowPrivateBox.Text = string.Join(Environment.NewLine, whitelist.Allow.Private);
        WhitelistHintText.Text = $"管理员：{whitelist.OwnerQQ?.ToString() ?? "未设置"}";
    }

    // 载入群列表并按当前白名单勾选。
    private async Task LoadGroupsAsync()
    {
        var groups = await _client.GetGroupsAsync();
        var allowed = (await _client.GetWhitelistAsync())?.Allow.Groups ?? [];

        _groups.Clear();
        foreach (var group in groups)
        {
            _groups.Add(new GroupItem(group.Id, group.Name));
        }

        // 勾选已允许的群（按群号匹配）。
        foreach (var item in _groups)
        {
            if (allowed.Contains(item.Id, StringComparer.Ordinal))
            {
                GroupList.SelectedItems.Add(item);
            }
        }
    }

    // 载入人设。
    private async Task LoadPersonaAsync()
    {
        var response = await _client.GetPersonaAsync();
        if (response is null)
        {
            return;
        }

        ApplyPersonaLabels(response);
        PersonaEnabledCheck.IsChecked = response.Persona.Enabled;
        PersonaNameBox.Text = response.Persona.Name;
        PersonaToneBox.Text = response.Persona.Tone;
        PersonaStyleBox.Text = response.Persona.Style;
        PersonaForbidBox.Text = response.Persona.Forbid;
        PersonaExtraBox.Text = response.Persona.Extra;
        PersonaNoteText.Text = response.Note ?? string.Empty;
    }

    // 载入配额。
    private async Task LoadQuotaAsync()
    {
        var quota = await _client.GetQuotaAsync();
        if (quota is null)
        {
            return;
        }

        QuotaEnabledCheck.IsChecked = quota.Enabled;
        GroupWarnBox.Text = quota.GroupWarnAt.ToString();
        GroupLimitBox.Text = quota.GroupLimit.ToString();
        PrivateWarnBox.Text = quota.PrivateWarnAt.ToString();
        PrivateLimitBox.Text = quota.PrivateLimit.ToString();
        ExemptBox.Text = string.Join(Environment.NewLine, quota.ExemptOwners);
        WarnTextBox.Text = quota.WarnText;
        WarnMessageBox.Text = quota.WarnMessage;
        QuotaImageBox.Text = quota.ImageConfigValue ?? string.Empty;
        QuotaEffectiveImageText.Text = string.IsNullOrWhiteSpace(quota.EffectiveImage)
            ? "（桥接未报告）"
            : quota.EffectiveImage;
        TodayUsageText.Text = $"群 {quota.TodayGroup} 条 / 私聊 {quota.TodayPrivate} 条";

        // 留档：保存时用来比对，防止整体提交把未显示的字段清空。
        _quotaBaseline = quota;
    }

    // 人设字段标签由桥接下发的 fields[].label 决定 —— 界面上不能写死。
    // 桥接改了文案（措辞、顺序）而界面不改，用户看到的说明就会与实践脱节。
    private void ApplyPersonaLabels(PersonaResponse response)
    {
        // key → 界面上对应的标签控件
        var labels = new Dictionary<string, TextBlock>(StringComparer.Ordinal)
        {
            ["name"] = PersonaNameLabel,
            ["tone"] = PersonaToneLabel,
            ["style"] = PersonaStyleLabel,
            ["forbid"] = PersonaForbidLabel,
            ["extra"] = PersonaExtraLabel,
        };

        foreach (var field in response.Fields)
        {
            if (labels.TryGetValue(field.Key, out var block) && !string.IsNullOrWhiteSpace(field.Label))
            {
                block.Text = field.Label;
            }
        }
    }

    // 载入 token 汇总与最近活动。
    private async Task LoadTokensAsync()
    {
        var summary = await _client.GetTokenSummaryAsync();
        if (summary is null)
        {
            TokenSummaryText.Text = "未能读取 token 汇总。";
        }
        else
        {
            // 分项计数在 totals.buckets 里面，不在 totals 顶层 —— 详见 TokenTotals 的注释。
            var totals = summary.Totals;
            var buckets = totals?.Buckets;
            var symbol = summary.Symbol ?? "¥";
            var tier = summary.NowTier?.TierLabel ?? "未知";
            var model = summary.NowTier?.Label ?? "?";

            var lines = new List<string>
            {
                $"计价档位 : {tier}（{model}）",
                $"输入命中 : {buckets?.CacheHit ?? 0:N0}",
                $"输入未命中: {buckets?.CacheMiss ?? 0:N0}",
            };

            // 缓存写入与推理 token 通常为 0，非零时才占一行，避免把常用信息挤下去。
            if ((buckets?.CacheWrite ?? 0) > 0)
            {
                lines.Add($"缓存写入 : {buckets!.CacheWrite ?? 0:N0}");
            }

            if ((buckets?.Reasoning ?? 0) > 0)
            {
                lines.Add($"推理     : {buckets!.Reasoning ?? 0:N0}");
            }

            lines.Add($"输出     : {buckets?.Output ?? 0:N0}");
            lines.Add($"合计     : {totals?.TotalTokens ?? 0:N0} tokens");

            if (totals?.CacheHitRate is { } rate)
            {
                lines.Add($"缓存命中率: {rate * 100:0.##}%");
            }

            if (totals?.Turns is { } turns && totals.Steps is { } steps)
            {
                lines.Add($"轮次     : {turns:N0}（{steps:N0} 步 / {totals.Conversations ?? 0} 个会话）");
            }

            // 金额是桥接按价目表折算的估算值时就说明，别让用户当成账单。
            var estimateNote = totals?.Estimated == true ? "　（按价目表折算）" : string.Empty;
            lines.Add($"折算金额 : {symbol}{totals?.Cost ?? 0:0.####}{estimateNote}");
            lines.Add("（以上为账本全部，不限时段）");

            TokenSummaryText.Text = string.Join(Environment.NewLine, lines);
        }

        var status = await _client.GetStatusAsync();
        ActivityText.Text = string.IsNullOrWhiteSpace(status?.Activity) ? "（暂无活动）" : status.Activity;

        _currentMode = status?.Mode;

        var roles = await _client.GetRolesAsync();
        _roleInjectLimit = roles?.Limits?.InjectMaxChars ?? 0;

        _sessionsSnapshot = await _client.GetSessionsAsync();
        _sessions.Clear();
        foreach (var session in _sessionsSnapshot)
        {
            _sessions.Add(session);
        }
        // 模型与推理强度：可选档位与模型清单都由桥接按 DSH 实际公布的能力下发。
        var selection = await _client.GetModelSelectionAsync();
        var catalog = await _client.GetModelCatalogAsync();

        if (selection is not null)
        {
            // 用了具体类型 List<string>：这里若写成 [] 会被推断为 string[]，与下面三元表达式的 List<string> 类型不一致。
            var options = selection.Options.Count > 0
                ? selection.Options
                : new List<string> { "low", "high", "max" };
            EffortCombo.ItemsSource = options;
            var current = ChooseEffort(selection.ReasoningEffort);
            EffortCombo.SelectedItem = options.FirstOrDefault(o => string.Equals(o, current, StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault();
            ModelText.Text = $"当前 {selection.Provider}/{selection.Model}";   // 赋值而非 +=，否则每次刷新都在后面接一截
        }

        if (catalog is not null && catalog.Models.Count > 0)
        {
            ModelCombo.ItemsSource = catalog.Models;
            ModelCombo.SelectedItem = catalog.Models
                .FirstOrDefault(m => string.Equals(m.Id, catalog.Model, StringComparison.OrdinalIgnoreCase))
                ?? catalog.Models[0];
        }

        _modelBaseline = selection;
        UpdateModelHint();
    }

    // 下拉框变化：只更新提示，不自动提交（避免误点就改掉全局默认）。
    private void OnModelChanged(object sender, SelectionChangedEventArgs e) => UpdateModelHint();

    private void OnEffortChanged(object sender, SelectionChangedEventArgs e) => UpdateModelHint();

    // 提示当前选择与是否未保存。两处都选好才做比对，未选好时不打扰。
    private void UpdateModelHint()
    {
        var model = ModelCombo.SelectedItem as DshModelOption;
        var effort = EffortCombo.SelectedItem as string;

        if (model is null || string.IsNullOrWhiteSpace(effort) || _modelBaseline is null)
        {
            return;
        }

        var changed = !string.Equals(model.Id, _modelBaseline.Model, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(effort, _modelBaseline.ReasoningEffort, StringComparison.OrdinalIgnoreCase);

        ModelText.Text = changed
            ? $"当前 {_modelBaseline.Provider}/{_modelBaseline.Model} · {_modelBaseline.ReasoningEffort} → 待保存 {model.Id} · {effort}"
            : $"当前 {_modelBaseline.Provider}/{_modelBaseline.Model} · {_modelBaseline.ReasoningEffort}";
    }

    // 切换运行模式。
    private async void OnApplyModeClick(object sender, RoutedEventArgs e)
    {
        var index = ModeCombo.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        var mode = Modes[index].Value;
        var ok = await _client.SetModeAsync(mode);
        StatusText.Text = ok ? $"已切换到 {Modes[index].Label}" : "切换模式失败（详见桥接日志）。";
    }

    // 保存模型与推理强度（写入 DSH 全局默认，对已有会话于下一条消息生效）。
    // 桥接在 enabled=true 时要求 model 与 reasoningEffort 同时有效，所以这里一次提交两者。
    private async void OnSaveModelClick(object sender, RoutedEventArgs e)
    {
        var model = ModelCombo.SelectedItem as DshModelOption;
        if (model is null)
        {
            StatusText.Text = "请先选择模型。";
            return;
        }

        var effort = ChooseEffort(EffortCombo.SelectedItem?.ToString());
        if (effort is null)
        {
            StatusText.Text = "请先选择推理强度。";
            return;
        }

        var unchanged = _modelBaseline is not null
            && string.Equals(model.Id, _modelBaseline.Model, StringComparison.OrdinalIgnoreCase)
            && string.Equals(effort, _modelBaseline.ReasoningEffort, StringComparison.OrdinalIgnoreCase);

        if (unchanged)
        {
            StatusText.Text = "与当前设置相同，无需保存。";
            return;
        }

        var ok = await _client.SaveModelSelectionAsync(true, model.Id, effort);
        if (!ok)
        {
            StatusText.Text = "保存失败：桥接未接受（模型标识或强度档位不合法）。";
            return;
        }

        StatusText.Text = $"已保存：{model.Id} · {effort}（下一条消息生效）";
        await LoadTokensAsync();   // 重新取回桥接侧最终值（模型目录与单价都在这条路径里刷新）
    }

    // 把界面文本映射为可保存的强度档位；非法输入返回 null。
    private static string? ChooseEffort(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var effort in new[] { "low", "high", "max" })
        {
            if (text.Contains(effort, StringComparison.OrdinalIgnoreCase))
            {
                return effort;
            }
        }

        return null;
    }

    // 保存白名单。
    private async void OnSaveWhitelistClick(object sender, RoutedEventArgs e)
    {
        var allowPrivate = SplitLines(AllowPrivateBox.Text);
        var allowGroups = GroupList.SelectedItems.Cast<GroupItem>().Select(g => g.Id).ToList();

        var existing = await _client.GetWhitelistAsync();
        var ok = await _client.SaveWhitelistAsync(allowPrivate, allowGroups, existing?.OwnerQQ);

        WhitelistHintText.Text = ok
            ? $"已保存：群 {allowGroups.Count} 个 / 私聊 {allowPrivate.Count} 个（即时生效）"
            : "保存失败：请检查桥接是否在运行。";
    }

    // 保存人设。
    private async void OnSavePersonaClick(object sender, RoutedEventArgs e)
    {
        var persona = new PersonaConfig
        {
            Enabled = PersonaEnabledCheck.IsChecked == true,
            Name = PersonaNameBox.Text,
            Tone = PersonaToneBox.Text,
            Style = PersonaStyleBox.Text,
            Forbid = PersonaForbidBox.Text,
            Extra = PersonaExtraBox.Text,
        };

        var ok = await _client.SavePersonaAsync(persona);

        // 桥接会拒绝"包含改写安全规则意图"的内容，因此失败时给出这个具体解释。
        PersonaHintText.Text = ok
            ? "已保存（下一次回复生效）"
            : "保存被拒绝：内容可能包含改写安全规则的意图，或桥接未运行。";
    }

    // 保存配额。
    private async void OnSaveQuotaClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseInt(GroupWarnBox.Text, out var groupWarn)
            || !TryParseInt(GroupLimitBox.Text, out var groupLimit)
            || !TryParseInt(PrivateWarnBox.Text, out var privateWarn)
            || !TryParseInt(PrivateLimitBox.Text, out var privateLimit))
        {
            QuotaHintText.Text = "保存失败：预警点与上限必须是整数。";
            return;
        }

        // 桥接的配额保存是**整体覆盖**，未提交的字段会被清空。
        // 「上限提示语」在界面上容易被视为可选，一旦被清掉，达到上限时就没有那句话了，
        // 所以这里显式确认一次，而不是默默提交。
        var warnMessage = WarnMessageBox.Text.Trim();
        if (warnMessage.Length == 0 && !string.IsNullOrWhiteSpace(_quotaBaseline?.WarnMessage))
        {
            var answer = System.Windows.MessageBox.Show(
                "「上限提示语」已被清空，保存后将不再有这句话。仍要保存吗？",
                "确认清空",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                QuotaHintText.Text = "已取消保存。";
                return;
            }
        }

        var quota = new QuotaConfig
        {
            Enabled = QuotaEnabledCheck.IsChecked == true,
            GroupWarnAt = groupWarn,
            GroupLimit = groupLimit,
            PrivateWarnAt = privateWarn,
            PrivateLimit = privateLimit,
            ExemptOwners = SplitLines(ExemptBox.Text),
            WarnText = WarnTextBox.Text,
            WarnMessage = warnMessage,
            ImageConfigValue = string.IsNullOrWhiteSpace(QuotaImageBox.Text) ? null : QuotaImageBox.Text.Trim(),
        };

        var ok = await _client.SaveQuotaAsync(quota);
        QuotaHintText.Text = ok ? "已保存（即时生效）" : "保存失败：请检查桥接是否在运行。";
        if (ok)
        {
            // 重新载入以便把桥接侧计算出的 effectiveImage 等字段同步回来。
            await LoadQuotaAsync();
        }
    }

    // 上限提示语为空时给出可见提醒（保存时还会再确认一次）。
    private void OnWarnMessageChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        QuotaHintText.Text = WarnMessageBox.Text.Trim().Length == 0
            ? "注意：上限提示语为空 —— 达到上限时不会自动发出提示。"
            : string.Empty;
    }

    // 把多行文本拆成去空的列表。
    private static List<string> SplitLines(string text) => text
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    // 宽松的整数解析：失败返回 false 而不抛异常。
    private static bool TryParseInt(string text, out int value)
        => int.TryParse(text.Trim(), out value);
    // ── 角色（人格）──

    // 角色列表。
    private readonly ObservableCollection<RoleSummary> _roles = [];

    // 黑话词条（已按筛选条件过滤）。
    private readonly ObservableCollection<SlangEntry> _slang = [];

    // 全部黑话词条（原始数据，筛选时从这里取）。
    private List<SlangEntry> _allSlang = [];

    // 程序设置 RoleContentBox 时抑制 TextChanged，避免污染状态提示。
    private bool _suppressRoleTextChanged;

    // 载入角色列表。
    private async Task LoadRolesAsync()
    {
        var response = await _client.GetRolesAsync();
        if (response is null)
        {
            RoleStatusText.Text = "未能读取角色列表。";
            return;
        }

        _roles.Clear();
        foreach (var summary in response.Summaries)
        {
            _roles.Add(summary);
        }

        RoleStatusText.Text = response.Current is null
            ? $"共 {_roles.Count} 个角色，当前未启用。"
            : $"共 {_roles.Count} 个角色，当前启用：{response.Current}";

        if (response.Limits is { } limits)
        {
            RoleLimitText.Text = $"限制：名称 ≤ {limits.NameMax} 字，正文 ≤ {limits.ContentMaxBytes / 1024} KB，注入 ≤ {limits.InjectMaxChars} 字";
        }
    }

    // 选中角色时载入其正文。
    private async void OnRoleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RoleList.SelectedItem is not RoleSummary summary)
        {
            return;
        }

        var content = await _client.GetRoleContentAsync(summary.Name);
        if (content is null)
        {
            RoleStatusText.Text = $"未能读取「{summary.Name}」的正文。";
            return;
        }

        _suppressRoleTextChanged = true;
        RoleNameBox.Text = content.Name;
        RoleContentBox.Text = content.Content;
        _suppressRoleTextChanged = false;

        UpdateRoleInjectHint();
    }

    // 正文变化时实时提示注入字符数（超出上限会在实际对话中被截断）。
    private void OnRoleContentChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressRoleTextChanged)
        {
            return;
        }

        UpdateRoleInjectHint();
    }

    private void UpdateRoleInjectHint()
    {
        var chars = RoleContentBox.Text.Length;
        RoleInjectHint.Text = $"当前正文 {chars} 字。超过注入上限的部分不会进入对话上下文；"
            + "含「一代专用指令」的内容会在二代仿真模式下被跳过。";
    }

    private async void OnRoleReloadClick(object sender, RoutedEventArgs e) => await LoadRolesAsync();

    // 启用选中角色。
    private async void OnRoleEnableClick(object sender, RoutedEventArgs e)
    {
        if (RoleList.SelectedItem is not RoleSummary summary)
        {
            RoleStatusText.Text = "请先在列表中选择一个角色。";
            return;
        }

        var ok = await _client.SetCurrentRoleAsync(summary.Name);
        if (!ok)
        {
            RoleStatusText.Text = "启用失败。";
            return;
        }

        // 必须重新载入：列表里会标出"当前启用"，不刷新的话它会一直显示旧状态，
        // 用户看到的是"点了启用但列表没变化"。
        await LoadRolesAsync();
        RoleStatusText.Text = $"已启用「{summary.Name}」（下一条消息生效）";
    }

    // 清除当前角色。
    private async void OnRoleClearClick(object sender, RoutedEventArgs e)
    {
        var ok = await _client.SetCurrentRoleAsync(string.Empty);
        if (!ok)
        {
            RoleStatusText.Text = "清除失败。";
            return;
        }

        await LoadRolesAsync();   // 同上：让列表的"当前启用"标记同步
        RoleStatusText.Text = "已清除角色（恢复为纯仿真提示词）。";
    }

    // 新建角色：清空编辑区，用户填名字与正文后点「保存正文」。
    private void OnRoleNewClick(object sender, RoutedEventArgs e)
    {
        _suppressRoleTextChanged = true;
        RoleNameBox.Text = string.Empty;
        RoleContentBox.Text = string.Empty;
        _suppressRoleTextChanged = false;

        RoleList.SelectedItem = null;
        RoleStatusText.Text = "已清空编辑区：填写名称与正文后点「保存正文」即创建新角色。";
    }

    // 保存角色正文（不存在时由后端按新建处理）。
    private async void OnRoleSaveClick(object sender, RoutedEventArgs e)
    {
        var name = RoleNameBox.Text.Trim();
        if (name.Length == 0)
        {
            RoleStatusText.Text = "角色名不能为空。";
            return;
        }

        var ok = await _client.SaveRoleAsync(name, RoleContentBox.Text);
        RoleStatusText.Text = ok ? $"已保存「{name}」。" : "保存失败（名称或正文可能超出限制）。";
        if (ok)
        {
            await LoadRolesAsync();
        }
    }

    // 删除选中角色。
    private async void OnRoleDeleteClick(object sender, RoutedEventArgs e)
    {
        if (RoleList.SelectedItem is not RoleSummary summary)
        {
            RoleStatusText.Text = "请先选择要删除的角色。";
            return;
        }

        var ok = await _client.DeleteRoleAsync(summary.Name);
        RoleStatusText.Text = ok ? $"已删除「{summary.Name}」。" : "删除失败。";
        if (ok)
        {
            await LoadRolesAsync();
        }
    }

    // 重命名：用编辑区里的名字作为新名。
    private async void OnRoleRenameClick(object sender, RoutedEventArgs e)
    {
        if (RoleList.SelectedItem is not RoleSummary summary)
        {
            RoleStatusText.Text = "请先选择要改名的角色。";
            return;
        }

        var newName = RoleNameBox.Text.Trim();
        if (newName.Length == 0 || newName == summary.Name)
        {
            RoleStatusText.Text = "请先把编辑区的名称改成新名字，再点「改名」。";
            return;
        }

        var ok = await _client.RenameRoleAsync(summary.Name, newName);
        RoleStatusText.Text = ok ? $"已改名为「{newName}」。" : "改名失败。";
        if (ok)
        {
            await LoadRolesAsync();
        }
    }

    // ── 黑话词库 ──

    // 载入黑话词条。
    private async Task LoadSlangAsync()
    {
        var response = await _client.GetSlangAsync();
        if (response is null)
        {
            SlangStatusText.Text = "未能读取黑话词库。";
            return;
        }

        _allSlang = response.Entries;
        ApplySlangFilter();

        var candidates = _allSlang.Count(x => x.IsCandidate);
        SlangStatusText.Text = $"共 {_allSlang.Count} 条，其中候选 {candidates} 条。";
    }

    // 按状态下拉框过滤。
    private void ApplySlangFilter()
    {
        var filter = SlangFilterCombo.SelectedItem?.ToString() ?? "全部";
        var items = filter switch
        {
            "仅候选" => _allSlang.Where(x => x.IsCandidate),
            "仅已确认" => _allSlang.Where(x => !x.IsCandidate),
            _ => _allSlang,
        };

        _slang.Clear();
        foreach (var entry in items)
        {
            _slang.Add(entry);
        }

        SlangDetailText.Text = $"当前显示 {_slang.Count} 条。候选词条来自群里出现的表达，"
            + "确认后会进入提示词供 AI 参考；拒绝或删除则不再参与。";
    }

    private void OnSlangFilterChanged(object sender, SelectionChangedEventArgs e) => ApplySlangFilter();

    private async void OnSlangReloadClick(object sender, RoutedEventArgs e) => await LoadSlangAsync();

    // 触发一次提取（耗时较长，桥接在后台跑）。
    private async void OnSlangExtractClick(object sender, RoutedEventArgs e)
    {
        SlangStatusText.Text = "已请求提取，稍后刷新查看结果…";
        var ok = await _client.ExtractSlangAsync();
        if (!ok)
        {
            SlangStatusText.Text = "提取请求失败（桥接未运行或该功能已关闭）。";
        }
    }

    // 取选中的词条 ID；未选中时给出提示。
    private List<string>? SelectedSlangIds()
    {
        var ids = SlangList.SelectedItems.Cast<SlangEntry>().Select(x => x.Id).ToList();
        if (ids.Count == 0)
        {
            SlangActionHint.Text = "请先在列表中选择词条（可按住 Ctrl 多选）。";
            return null;
        }

        return ids;
    }

    private async void OnSlangConfirmClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSlangIds() is not { } ids)
        {
            return;
        }

        var ok = await _client.ConfirmSlangAsync(ids);
        SlangActionHint.Text = ok ? $"已确认 {ids.Count} 条。" : "确认失败。";
        if (ok)
        {
            await LoadSlangAsync();
        }
    }

    private async void OnSlangRejectClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSlangIds() is not { } ids)
        {
            return;
        }

        var ok = await _client.RejectSlangAsync(ids);
        SlangActionHint.Text = ok ? $"已拒绝 {ids.Count} 条。" : "拒绝失败。";
        if (ok)
        {
            await LoadSlangAsync();
        }
    }

    private async void OnSlangDeleteClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSlangIds() is not { } ids)
        {
            return;
        }

        var ok = await _client.DeleteSlangAsync(ids);
        SlangActionHint.Text = ok ? $"已删除 {ids.Count} 条。" : "删除失败。";
        if (ok)
        {
            await LoadSlangAsync();
        }
    }

    // ── 仿真提示词 ──

    // 可选预设（与桥接的 preset 名一致）。
    private static readonly string[] Presets = ["qq-chat", "qq-chat-v2"];

    private async void OnPresetLoadClick(object sender, RoutedEventArgs e) => await LoadPresetAsync();

    // 从桥接读回提示词正文。载入与保存后都走它，保证界面显示的就是桥接实际持有的内容。
    private async Task LoadPresetAsync()
    {
        var preset = PresetCombo.SelectedItem?.ToString() ?? Presets[^1];
        var response = await _client.GetSimPromptAsync(preset);

        if (response is null)
        {
            PresetStatusText.Text = $"载入失败：找不到预设 {preset}。";
            return;
        }

        PresetContentBox.Text = response.Content;

        var status = response.Status;
        PresetStatusText.Text = status is null
            ? $"已载入 {preset}（{response.Content.Length} 字）"
            : $"已载入 {status.Label ?? preset}（{response.Content.Length} 字）"
              + (status.RequiresRestart ? "；保存后需重启 DSH 生效" : string.Empty);
    }

    private async void OnPresetSaveClick(object sender, RoutedEventArgs e)
    {
        var preset = PresetCombo.SelectedItem?.ToString() ?? Presets[^1];
        var ok = await _client.SaveSimPromptAsync(preset, PresetContentBox.Text);

        // 桥接会做不变量校验，被拒时通常是删掉了权限边界表述或正文过短。
        if (!ok)
        {
            PresetStatusText.Text = "保存被拒绝：可能缺少安全不变量（权限边界、令牌、角色注入等）或正文过短。";
            return;
        }

        // 保存成功后回读一次：桥接可能对正文做了规范化，直接把输入框里的内容当成
        // "已保存的内容"会让界面与桥接不一致 —— 用户下次看到的就是自己以为的版本。
        await LoadPresetAsync();
        PresetStatusText.Text = $"已保存 {preset}；需重启 DSH 生效。";
    }
    // ── 「她是谁」一致性审计 ──

    // 刷新三层的一致性提示与上下文占用。
    //
    // 这里读的是**界面上当前的内容**而不是桥接里的值：用户正在编辑时也想立刻看到
    // 「改完会不会冲突、占多大」，而不是等保存后才知道。
    private void UpdateIdentityAudit()
    {
        // 人设字段（key 与桥接 fields[].key 一致）。
        var personaValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = PersonaNameBox.Text,
            ["tone"] = PersonaToneBox.Text,
            ["style"] = PersonaStyleBox.Text,
            ["forbid"] = PersonaForbidBox.Text,
            ["extra"] = PersonaExtraBox.Text,
        };

        // 黑话表：桥接只注入排名靠前的若干条（cfg.slang.injectMax，默认 8），
        // 不是把整个词库都塞进去 —— 早先按"全部词条 × 每条估 40 字"算，
        // 在有 130+ 条候选时会高估到 5000+ 字，让预算看起来吓人。
        var slangTopN = Math.Min(_allSlang.Count(e => !string.IsNullOrWhiteSpace(e.Content)), 8);
        var slangChars = slangTopN * 40;

        var input = new Models.IdentityAuditor.Input(
            Persona: new Models.PersonaConfig { Enabled = PersonaEnabledCheck.IsChecked == true },
            PersonaValues: personaValues,
            RoleName: RoleList.SelectedItem is RoleSummary s ? s.Name : null,
            RoleContent: RoleContentBox.Text,
            SimPrompt: PresetContentBox.Text,
            SimPromptPreset: PresetCombo.SelectedItem?.ToString(),
            Mode: null,                       // 由 ApplyStatus 缓存的当前模式填
            SlangChars: slangChars);

        // 当前运行模式影响「一代指令」是否算冲突，必须带上。
        input = input with { Mode = _currentMode };

        var report = Models.IdentityAuditor.Audit(input);

        IdentityWarningText.Text = report.Warnings.Count == 0
            ? "✓ 没发现冲突。"
            : string.Join(Environment.NewLine + Environment.NewLine, report.Warnings.Select(w => "⚠ " + w));
        IdentityWarningText.Foreground = report.Warnings.Count == 0
            ? System.Windows.Media.Brushes.Gray
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xD1, 0x34, 0x38));

        var b = report.Budget;
        var roleLimit = _roleInjectLimit > 0 ? _roleInjectLimit : 6000;
        var presetLimit = 10000;   // 预设模板约 10 KB，按一个便于比较的参考值

        IdentityBudgetText.Text =
            $"上下文占用（字符）：人设 {b.PersonaChars} · 角色卡 {b.RoleChars} · 提示词 {b.SimPromptChars}"
            + (b.SlangChars > 0 ? $" · 黑话表约 {b.SlangChars}" : string.Empty)
            + $"　合计约 {b.TotalChars:N0}。角色卡与提示词每条唤醒都会进入上下文，"
            + "角色卡越大每条消息的成本越高。";

        SetBudgetBar(PersonaBudgetBar, PersonaBudgetText, b.PersonaChars, b.PersonaLimit);
        SetBudgetBar(RoleBudgetBar, RoleBudgetText, b.RoleChars, roleLimit);
        SetBudgetBar(PresetBudgetBar, PresetBudgetText, b.SimPromptChars, presetLimit);
    }

    // 更新一条占用条：超限变红、接近变黄、正常保持主题色。
    private static void SetBudgetBar(
        System.Windows.Controls.ProgressBar bar, System.Windows.Controls.TextBlock label, int used, int limit)
    {
        var percent = limit <= 0 ? 0 : Math.Min(100, (int)Math.Round(used * 100.0 / limit));
        bar.Value = percent;
        label.Text = limit <= 0
            ? $"{used} 字"
            : $"{used} / {limit} 字（{percent}%）";

        bar.Foreground = used > limit
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD1, 0x34, 0x38))
            : percent >= 80
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCA, 0x8A, 0x04))
                : AccentBrush;
    }

    // ── 二代仿真 ──

    // 读取会话状态、活动开关与工具日志。
    private async Task LoadSocialV2Async()
    {
        await LoadSocialV2SwitchesAsync();

        var states = await _client.GetSocialV2StatesAsync();
        var activity = await _client.GetSocialV2ActivityAsync();

        if (states is null)
        {
            SocialV2HintText.Text = "未能读取二代仿真状态（桥接可能未运行）。";
            return;
        }

        // 保留选中项：定时刷新不该把用户的选择清掉。
        var selectedKey = (SocialV2ConversationList.SelectedItem as SocialV2Conversation)?.Key;

        SocialV2ConversationList.ItemsSource = states.Conversations;

        if (selectedKey is not null)
        {
            SocialV2ConversationList.SelectedItem =
                states.Conversations.FirstOrDefault(c => c.Key == selectedKey);
        }

        if (SocialV2ConversationList.SelectedItem is null && states.Conversations.Count > 0)
        {
            SocialV2ConversationList.SelectedIndex = 0;
        }

        var paused = activity?.Paused == true ? "　⚠ 二代仿真已暂停" : string.Empty;
        SocialV2HintText.Text = $"{states.Conversations.Count} 个会话{paused}";

        await LoadToolLogAsync();
        await LoadFeedbackAsync();
    }

    // 刷新开关状态时置位，避免那次赋值被当成"用户改了开关"。
    private bool _suppressSocialV2SwitchEvents;

    // 读取四个运行开关。
    //
    // 事件绑的是 Checked/Unchecked 而不是 Click，见 OnSocialV2SwitchChanged 的注释。
    // 所以这里的赋值必须挡住 —— 否则每次刷新都会当成"用户改了开关"再存一遍。
    // 标志位的设置与清除之间没有 await，不会被别的刷新插进来。
    private async Task LoadSocialV2SwitchesAsync()
    {
        var response = await _client.GetSocialV2ConfigAsync();

        if (response?.Config is null)
        {
            SocialV2SwitchHintText.Text = string.IsNullOrEmpty(_client.LastError)
                ? "未能读取运行开关（桥接可能未运行）。"
                : $"未能读取运行开关：{_client.LastError}";
            return;
        }

        var config = response.Config;

        _suppressSocialV2SwitchEvents = true;
        try
        {
            // 缺字段时按"开"显示：桥接的默认值就是开，显示成关会让人以为被谁关掉了。
            SocialV2EnabledCheck.IsChecked = config.Enabled ?? true;
            SocialV2ProactiveCheck.IsChecked = config.Proactive?.Enabled ?? true;
            SocialV2StickerCheck.IsChecked = config.Sticker?.Enabled ?? true;
            SocialV2VoiceCheck.IsChecked = config.Voice?.Enabled ?? true;
        }
        finally
        {
            _suppressSocialV2SwitchEvents = false;
        }

        SocialV2SwitchHintText.Text = DescribeSwitches();
    }

    // 某个运行开关被改动 -> 写回桥接。
    //
    // 为什么绑 Checked/Unchecked 而不是 Click：一开始用的是 Click，理由是
    // "程序化赋值不会触发 Click，所以不必再加防重入标志位"。但 Click 只在
    // **真实输入**（鼠标、键盘）时发生 —— UIAutomation 的 TogglePattern.Toggle()
    // 直接调 IsChecked 的 setter，Checked/Unchecked 会触发而 Click 不会。
    // 结果是"勾变了、没人保存"，界面上完全看不出异常（实测踩到过）。
    //
    // 凡是会改变这个值的路径都应该走保存，所以绑 Checked/Unchecked；
    // 刷新时的那次赋值由 _suppressSocialV2SwitchEvents 挡掉。
    private async void OnSocialV2SwitchChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSocialV2SwitchEvents || !IsLoaded || sender is not CheckBox box)
        {
            return;
        }

        var on = box.IsChecked == true;

        // section 为 null 表示改的是顶层字段。
        string? section;
        string label;

        if (ReferenceEquals(box, SocialV2EnabledCheck))
        {
            section = null;
            label = "二代仿真总开关";
        }
        else if (ReferenceEquals(box, SocialV2ProactiveCheck))
        {
            section = "proactive";
            label = "主动冒泡";
        }
        else if (ReferenceEquals(box, SocialV2StickerCheck))
        {
            section = "sticker";
            label = "表情包";
        }
        else
        {
            section = "voice";
            label = "语音";
        }

        box.IsEnabled = false;
        try
        {
            // 走 Patch 而不是 Save：桥接是浅合并，交子对象会整体替换它，
            // 只交 {proactive:{enabled:false}} 会把 proactive 里其它字段清成默认值。
            var ok = await _client.PatchSocialV2ConfigAsync(section, "enabled", on);

            if (!ok)
            {
                // 失败要把勾弹回去 —— 否则界面显示"改了"，实际没改。
                // 这次赋值同样要挡住，不然会再触发一次本方法，形成来回重试。
                _suppressSocialV2SwitchEvents = true;
                try
                {
                    box.IsChecked = !on;
                }
                finally
                {
                    _suppressSocialV2SwitchEvents = false;
                }

                SocialV2SwitchHintText.Text = string.IsNullOrEmpty(_client.LastError)
                    ? $"没能改动「{label}」。"
                    : $"没能改动「{label}」：{_client.LastError}";
                return;
            }
        }
        finally
        {
            box.IsEnabled = true;
        }

        // 回读一遍：这不是多余的往返，而是确认改动真的落地了。
        // 桥接有一套"HTTP 200 但不生效"的历史（见 POST /api/role 那次），
        // 只看返回码不足以说明问题。实测也发现过"勾变了但配置没变"。
        await LoadSocialV2SwitchesAsync();
        SocialV2SwitchHintText.Text += $"　（刚改动：{label} → {(on ? "开" : "关")}）";
    }

    // 把四个开关拼成一句话。
    // 关着的那几项要单独列出来 —— 排查"她怎么不主动说话"时，
    // 恰恰是这些"没开"的项最容易被忽略。
    private string DescribeSwitches()
    {
        if (SocialV2EnabledCheck.IsChecked != true)
        {
            return "总开关已关闭：她的二代能力整体停用，工具调用也不会发生。";
        }

        var off = new List<string>();
        if (SocialV2ProactiveCheck.IsChecked != true)
        {
            off.Add("主动冒泡");
        }

        if (SocialV2StickerCheck.IsChecked != true)
        {
            off.Add("表情包");
        }

        if (SocialV2VoiceCheck.IsChecked != true)
        {
            off.Add("语音");
        }

        return off.Count == 0
            ? "总开关开着，其余三项也都开着。"
            : $"总开关开着；已关闭：{string.Join("、", off)}。";
    }

    // 工具调用日志是排查"她为什么不说话"的第一入口。
    private async Task LoadToolLogAsync()
    {
        var log = await _client.GetSocialV2ToolLogAsync();

        if (log is null)
        {
            SocialV2ToolLogText.Text = "未能读取工具日志。";
            return;
        }

        SocialV2ToolLogText.Text = log.Entries.Count == 0
            ? "（暂无记录）"
            : string.Join(Environment.NewLine, log.Entries.AsEnumerable().Reverse().Select(e => e.Display));
    }

    private async void OnRefreshSocialV2Click(object sender, RoutedEventArgs e) => await LoadSocialV2Async();

    private async void OnClearToolLogClick(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            $"清空工具调用日志？{Environment.NewLine}{Environment.NewLine}"
            + "它只记录工具调用历史，清掉不影响她的行为；但排查问题时就没得看了。",
            "确认清空",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            SocialV2HintText.Text = "已取消。";
            return;
        }

        var ok = await _client.ClearSocialV2ToolLogAsync();
        SocialV2HintText.Text = ok ? "工具日志已清空。" : "清空失败。";

        if (ok)
        {
            await LoadToolLogAsync();
        }
    }

    // 选中会话后拉它的状态明细与记忆。
    private async void OnSocialV2SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SocialV2ConversationList.SelectedItem is not SocialV2Conversation conversation
            || string.IsNullOrWhiteSpace(conversation.Key))
        {
            return;
        }

        var key = conversation.Key;

        // 用列表里已有的那份先说清唤醒配置，避免等网络回来才有内容。
        SocialV2WakeText.Text =
            $"会话：{key}{Environment.NewLine}{Environment.NewLine}"
            + (conversation.WakeConfig?.Display ?? "（读不到唤醒配置）")
            + Environment.NewLine + Environment.NewLine
            + "安全标志：" + (conversation.WakeSafety?.Display ?? "（无）")
            + Environment.NewLine + Environment.NewLine
            + $"未读 {conversation.UnreadCount}　最近消息 {conversation.RecentCount}"
            + Environment.NewLine
            + $"上次唤醒：{conversation.LastWakeReason ?? "（无记录）"}";

        var memory = await _client.GetSocialV2MemoryAsync(key);
        SocialV2MemoryText.Text = memory is null
            ? "（读不到记忆）"
            : string.IsNullOrWhiteSpace(memory.Formatted) ? "（她还没有关于这个会话的记忆）" : memory.Formatted;

        // 唤醒编辑器跟着选中项走 —— 否则很容易看着 A 会话的配置去改 B 会话。
        FillWakeEditor(conversation);
    }

    // 当前唤醒编辑器作用在哪个会话上。null 表示还没选中任何会话。
    private string? _wakeEditorKey;

    // 把某个会话的唤醒配置填进编辑器。
    //
    // 单独抽出来是因为有三条路径要填：切换选中项、点「读回」、保存成功后用桥接
    // 回传的那份覆盖（服务端会改写一些字段，只有回传的才是真正生效的值）。
    private void FillWakeEditor(SocialV2Conversation conversation)
    {
        var config = conversation.WakeConfig;
        var key = conversation.Key;

        _wakeEditorKey = key;
        WakeEditTargetText.Text = string.IsNullOrWhiteSpace(key) ? "（先在上面选中一个会话）" : key;

        if (config is null)
        {
            WakeEditHintText.Text = "这个会话没有可读的唤醒配置。";
            return;
        }

        WakeModeCombo.SelectedIndex = config.Mode == "active" ? 1 : 0;
        WakeInfiniteCheck.IsChecked = config.Infinite;

        var triggers = config.Triggers;
        WakeAtMentionCheck.IsChecked = triggers?.AtMention ?? false;
        WakeNameMentionCheck.IsChecked = triggers?.NameMention ?? false;
        WakeQuestionCheck.IsChecked = triggers?.Question ?? false;
        WakePokeCheck.IsChecked = triggers?.Poke ?? false;
        WakeAnyMessageCheck.IsChecked = triggers?.AnyMessage ?? false;

        WakeProbabilityBox.Text = (triggers?.Probability ?? 0).ToString("0.###", CultureInfo.InvariantCulture);
        WakeKeywordsBox.Text = string.Join(", ", triggers?.Keywords ?? []);
        WakeSpeakerIdsBox.Text = string.Join(", ", triggers?.SpeakerIds ?? []);
        WakeBatchSecondsBox.Text = (config.BatchWindowMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

        // 有限时长没法从 sleepUntil 直接反推（那是绝对时间），保持用户上次输入的值。
        if (string.IsNullOrWhiteSpace(WakeMinutesBox.Text))
        {
            WakeMinutesBox.Text = "30";
        }

        // 私聊里「指定成员」会被桥接清空 —— 与其让人填完发现没保存，不如直接停用。
        var isPrivate = key?.StartsWith("private:", StringComparison.Ordinal) == true;
        WakeSpeakerIdsBox.IsEnabled = !isPrivate;
        WakeSpeakerIdsLabel.Opacity = isPrivate ? 0.5 : 1.0;

        ApplyWakeModeConstraints();

        WakeEditHintText.Text = isPrivate
            ? "私聊没有「指定成员发言」这回事（桥接会清空它），其余项照常。"
            : "改完点「保存」。关键词按逗号分隔。";
    }

    // 活跃模式的连带效果。
    //
    // 桥接在 mode='active' 时会强制 infinite=true、sleepUntil=null、anyMessage=true
    // （`bridge.js:5453-5457`）。与其让人填一个不会被采纳的值、保存后再困惑于
    // 回读结果和刚才填的不一样，不如当场把这两个开关按服务端的结果摆好并停用。
    private void ApplyWakeModeConstraints()
    {
        var isActive = WakeModeCombo.SelectedIndex == 1;

        if (isActive)
        {
            WakeInfiniteCheck.IsChecked = true;
            WakeAnyMessageCheck.IsChecked = true;
        }

        WakeInfiniteCheck.IsEnabled = !isActive;
        WakeAnyMessageCheck.IsEnabled = !isActive;
        WakeMinutesBox.IsEnabled = WakeInfiniteCheck.IsChecked != true;
    }

    // 切模式时把连带效果摆出来（不切模式就不动用户填的东西）。
    private void OnWakeModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyWakeModeConstraints();
    }

    private async void OnReloadWakeConfigClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_wakeEditorKey))
        {
            WakeEditHintText.Text = "先在上面选中一个会话。";
            return;
        }

        await LoadWakeEditorAsync(_wakeEditorKey);
    }

    // 从桥接重新读某个会话的状态，再用它填编辑器。
    private async Task LoadWakeEditorAsync(string key)
    {
        var state = await _client.GetSocialV2StateAsync(key);

        if (state?.WakeConfig is null)
        {
            WakeEditHintText.Text = string.IsNullOrEmpty(_client.LastError)
                ? "读不到这个会话的唤醒配置。"
                : $"读不到唤醒配置：{_client.LastError}";
            return;
        }

        FillWakeEditor(new SocialV2Conversation
        {
            Key = key,
            WakeConfig = state.WakeConfig,
            WakeSafety = state.WakeSafety,
        });
    }

    private async void OnSaveWakeConfigClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_wakeEditorKey))
        {
            WakeEditHintText.Text = "先在上面选中一个会话。";
            return;
        }

        var key = _wakeEditorKey;
        var infinite = WakeInfiniteCheck.IsChecked == true;

        // 服务端会把 batchWindowMs 卡在 [1000, 3600000] 毫秒，sleepMs 卡在
        // [sleepMinMs=60000, sleepMaxMs=0（不限）]。这里先按同样的范围夹一次，
        // 免得存下去的值和界面上写的不是一回事。
        var batchSeconds = Math.Clamp(ToDouble(WakeBatchSecondsBox.Text, 12), 1, 3600);
        var minutes = Math.Max(1, ToDouble(WakeMinutesBox.Text, 30));

        var config = new Dictionary<string, object?>
        {
            ["mode"] = WakeModeCombo.SelectedIndex == 1 ? "active" : "diving",
            ["infinite"] = infinite,

            // 五个触发布尔全部显式提交：桥接在切回潜水时会清掉没提交的 anyMessage，
            // 提交一半会让"界面显示的"和"实际生效的"分叉。
            ["triggers"] = new Dictionary<string, object?>
            {
                ["atMention"] = WakeAtMentionCheck.IsChecked == true,
                ["nameMention"] = WakeNameMentionCheck.IsChecked == true,
                ["question"] = WakeQuestionCheck.IsChecked == true,
                ["poke"] = WakePokeCheck.IsChecked == true,
                ["anyMessage"] = WakeAnyMessageCheck.IsChecked == true,
                ["keywords"] = WakeListParser.ParseKeywords(WakeKeywordsBox.Text),
                ["speakerIds"] = WakeListParser.ParseSpeakerIds(WakeSpeakerIdsBox.Text),
                ["probability"] = Math.Clamp(ToDouble(WakeProbabilityBox.Text, 0), 0, 1),
            },
            ["batchWindowMs"] = (long)Math.Round(batchSeconds * 1000),
        };

        if (!infinite)
        {
            config["sleepMs"] = (long)Math.Round(minutes * 60_000);
        }

        SaveWakeConfigButton.IsEnabled = false;
        try
        {
            var result = await _client.SetSocialV2WakeConfigAsync(key, config);

            if (result is null || !result.Ok)
            {
                // 桥接的 error 原文比"保存失败"有用得多 —— 它常常直接说明了原因
                // （无限期潜水没有触发条件、还没走完沉睡前观察窗口……）。
                var reason = string.IsNullOrWhiteSpace(_client.LastError) ? "保存失败。" : _client.LastError;

                if (result?.PreSleepWaitRemainingMs is > 0)
                {
                    var remain = TimeSpan.FromMilliseconds(result.PreSleepWaitRemainingMs.Value);
                    reason += $"　还需约 {remain.TotalMinutes:0.#} 分钟。";
                }

                WakeEditHintText.Text = reason;
                return;
            }
        }
        finally
        {
            SaveWakeConfigButton.IsEnabled = true;
        }

        // 用桥接回传的那份覆盖编辑器：服务端会改写字段（活跃模式强制打开任意消息、
        // 关键词截断到 50 条、概率夹到 0~1……），只有回传的才是真正生效的值。
        await LoadWakeEditorAsync(key);

        // 会话列表与右侧明细也跟着更新，免得两处显示不一致。
        // 必须放在上面之后 —— LoadSocialV2Async 会重选会话并重填编辑器。
        await LoadSocialV2Async();

        // 提示放在最后写：上面两步都会把 WakeEditHintText 覆盖掉。
        WakeEditHintText.Text += "　（已保存并回读）";
    }

    private static double ToDouble(string? text, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    // 反馈是 AI 主动上报的问题。它和工具日志放在一起，因为两者都是"运行时留痕"。
    private async Task LoadFeedbackAsync()
    {
        var feedback = await _client.GetSocialV2FeedbackAsync();

        if (feedback is null)
        {
            SocialV2FeedbackText.Text = "未能读取反馈。";
            return;
        }

        SocialV2FeedbackText.Text = feedback.Entries.Count == 0
            ? "（暂无反馈）"
            : string.Join(Environment.NewLine, feedback.Entries.AsEnumerable().Reverse().Select(e => e.Display));
    }

    private async void OnClearFeedbackClick(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            $"清空 AI 反馈？{Environment.NewLine}{Environment.NewLine}"
            + "反馈是她主动上报的问题，清掉之后就找不回来了。",
            "确认清空",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            SocialV2HintText.Text = "已取消。";
            return;
        }

        var ok = await _client.ClearSocialV2FeedbackAsync();
        SocialV2HintText.Text = ok ? "反馈已清空。" : "清空失败。";

        if (ok)
        {
            await LoadFeedbackAsync();
        }
    }

    // ── 表情库 ──

    // 表情库是唯一一个"打开才值得拉"的分页：它的数据要经 OneBot 取，
    // 没人看的时候不该产生流量。所以第一次切过去时才加载。
    private bool _stickersLoaded;

    private async void OnConsoleTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 子控件（列表、下拉框）的选中变化也会冒泡到这里，只认 TabControl 自己的。
        if (!ReferenceEquals(e.OriginalSource, ConsoleTabs))
        {
            return;
        }

        if (ConsoleTabs.SelectedItem is System.Windows.Controls.TabItem { Header: "表情库" } && !_stickersLoaded)
        {
            _stickersLoaded = true;
            await LoadStickersAsync(null, ReadStickerCount());
        }

        // 语音库同理：列一次要给每个文件跑探测，没人看的时候不该做。
        if (ConsoleTabs.SelectedItem is System.Windows.Controls.TabItem { Header: "语音库" } && !_voicesLoaded)
        {
            _voicesLoaded = true;
            await LoadVoicesAsync();
        }
    }

    // 读取表情库。force=false 读缓存（先看库，再决定要不要花一次同步）。
    //
    // justSynced 用来区分两种"库是空的"：一种是还没同步过（该劝用户点同步），
    // 一种是刚同步完确实是空的（再劝就是死循环了）。
    private async Task LoadStickersAsync(string? query, int count, bool justSynced = false)
    {
        var result = await _client.GetStickersAsync(query, count);

        if (result is null)
        {
            StickerHintText.Text = "未能读取表情库（桥接可能未运行）。";
            return;
        }

        // 失败时桥接把原因放在 error 里，比"读到 0 条"有用得多。
        if (!result.Ok)
        {
            StickerHintText.Text = $"读取失败：{result.Error ?? "（无原因）"}";
            return;
        }

        StickerList.ItemsSource = result.Stickers;

        var trimmed = result.Truncated ? $"，已按上限截断（命中 {result.Matched} 条）" : string.Empty;
        var cached = result.FromCache ? "，来自缓存" : string.Empty;
        var synced = result.SyncedAt > 0
            ? $"，上次同步 {DateTimeOffset.FromUnixTimeMilliseconds(result.SyncedAt).ToLocalTime():MM-dd HH:mm}"
            : string.Empty;

        StickerHintText.Text = $"库中共 {result.Total} 条，本次显示 {result.Stickers.Count} 条{trimmed}{cached}{synced}。";

        if (result.Total == 0)
        {
            StickerHintText.Text += justSynced
                ? "　同步是成功的，说明这个 QQ 账号里确实没有收藏表情。"
                : "　点「强制同步」去问 OneBot 要收藏表情。";
        }
    }

    private string StickerQueryText() => StickerQueryBox.Text.Trim();

    private async void OnStickerQueryClick(object sender, RoutedEventArgs e)
    {
        await LoadStickersAsync(StickerQueryText(), ReadStickerCount());
    }

    // 强制同步是有副作用的写操作（拉 OneBot 收藏表情并落盘），所以单独一个按钮。
    //
    // 判断"这次同步到底成没成"只能靠比较 syncedAt 有没有前进：桥接的
    // /api/stickers/sync 在**同步失败时也返回 ok:true**（syncStickerLibrary 内部
    // 把错误 catch 掉并返回 null，接口用缓存兜底），所以 ok 字段说明不了任何事。
    // 不比对的话，同步失败和"同步成功但一条都没有"在界面上完全一样，
    // 用户会被反复劝去点那个什么也不会发生的按钮。
    private async void OnStickerSyncClick(object sender, RoutedEventArgs e)
    {
        StickerHintText.Text = "正在同步…";

        var before = await _client.GetStickersAsync(null, 1);
        var beforeAt = before?.SyncedAt ?? 0;

        var synced = await _client.SyncStickersAsync();

        if (synced is null)
        {
            StickerHintText.Text = $"同步失败：{_client.LastError ?? "（无响应）"}";
            return;
        }

        if (!synced.Ok)
        {
            StickerHintText.Text = $"同步失败：{synced.Error ?? "（无原因）"}";
            return;
        }

        if (synced.SyncedAt <= beforeAt)
        {
            // 时间戳没有前进 —— 同步没有真的发生。真正的原因在桥接日志里
            //（搜「[sticker]」），最常见的是 QQ 侧尚未登录，OneBot 的
            // fetch_custom_face_detail 直接失败了。
            StickerHintText.Text = "同步没有生效：桥接没能从 OneBot 取到收藏表情。"
                + "原因记在「日志」页（搜 [sticker]），最常见的是 QQ 侧还没登录。";
            return;
        }

        await LoadStickersAsync(StickerQueryText(), ReadStickerCount(), justSynced: true);
    }

    private void OnStickerSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (StickerList.SelectedItem is not StickerEntry sticker)
        {
            return;
        }

        StickerNoteBox.Text = sticker.LocalNote ?? string.Empty;
        StickerTagsBox.Text = sticker.TagsText;
        StickerUsageBox.Text = sticker.Usage ?? string.Empty;
        StickerRemarkBox.Text = sticker.Desc ?? string.Empty;
        StickerEditHintText.Text = string.Empty;

        var used = sticker.UseCount > 0
            ? $"用过 {sticker.UseCount} 次"
            : "还没用过";

        StickerDetailText.Text =
            $"匹配标识：{sticker.MatchId}{Environment.NewLine}"
            + $"来源：{sticker.Source ?? "（未知）"}　{used}";
    }

    // 保存本地笔记 / 标签 / 用法。这三项只存在桥接这边，写坏了也只影响她选表情。
    private async void OnSaveStickerNoteClick(object sender, RoutedEventArgs e)
    {
        if (StickerList.SelectedItem is not StickerEntry sticker)
        {
            StickerEditHintText.Text = "先在左边选一张表情。";
            return;
        }

        var id = sticker.MatchId;
        if (string.IsNullOrWhiteSpace(id))
        {
            StickerEditHintText.Text = "这条表情没有可用的标识（id/resId/md5/url 都缺失），无法保存。";
            return;
        }

        // 标签按空白切分 —— 与右侧输入框的提示一致（"空格分隔"）。
        var tags = StickerTagsBox.Text
            .Split([' ', '\t', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var saved = await _client.SaveStickerNoteAsync(
            id, StickerNoteBox.Text.Trim(), tags, StickerUsageBox.Text.Trim());

        if (saved is null || !saved.Ok)
        {
            StickerEditHintText.Text = saved is null
                ? $"保存失败：{_client.LastError ?? "（无响应）"}"
                : $"保存失败：{saved.Error ?? "（无原因）"}";
            return;
        }

        StickerEditHintText.Text = "已保存。";

        // 就地更新列表里的那一条，而不是整表重刷 —— 重刷会丢掉当前选中项，
        // 而用户接下来很可能要继续改这一张。
        if (saved.Sticker is not null)
        {
            sticker.LocalNote = saved.Sticker.LocalNote;
            sticker.Tags = saved.Sticker.Tags;
            sticker.Usage = saved.Sticker.Usage;

            var items = StickerList.ItemsSource as List<StickerEntry>;
            var index = items?.IndexOf(sticker) ?? -1;
            if (items is not null && index >= 0)
            {
                // 换掉实例并复位选中，逼 ListBox 用新的 Display 重画这一行。
                items[index] = sticker;
                StickerList.ItemsSource = null;
                StickerList.ItemsSource = items;
                StickerList.SelectedIndex = index;
            }
        }
    }

    // 改 QQ 备注：这是会写进用户 QQ 账号的操作，比本地笔记"重"，所以先确认。
    private async void OnSaveStickerRemarkClick(object sender, RoutedEventArgs e)
    {
        if (StickerList.SelectedItem is not StickerEntry sticker)
        {
            StickerEditHintText.Text = "先在左边选一张表情。";
            return;
        }

        var id = sticker.MatchId;
        if (string.IsNullOrWhiteSpace(id))
        {
            StickerEditHintText.Text = "这条表情没有可用的标识（id/resId/md5/url 都缺失），无法保存。";
            return;
        }

        var remark = StickerRemarkBox.Text.Trim();
        var answer = System.Windows.MessageBox.Show(
            $"把 QQ 备注改成「{remark}」？{Environment.NewLine}{Environment.NewLine}"
            + "这会同步到你的 QQ 账号（不只是 DSQQ 本地），手机上也能看到。",
            "确认修改 QQ 备注",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            StickerEditHintText.Text = "已取消。";
            return;
        }

        var saved = await _client.SaveStickerRemarkAsync(id, remark);

        if (saved is null || !saved.Ok)
        {
            StickerEditHintText.Text = saved is null
                ? $"保存失败：{_client.LastError ?? "（无响应）"}"
                : $"保存失败：{saved.Error ?? "（无原因）"}";
            return;
        }

        sticker.Desc = saved.Sticker?.Desc ?? remark;
        StickerEditHintText.Text = "QQ 备注已更新。";

        var items = StickerList.ItemsSource as List<StickerEntry>;
        var index = items?.IndexOf(sticker) ?? -1;
        if (items is not null && index >= 0)
        {
            items[index] = sticker;
            StickerList.ItemsSource = null;
            StickerList.ItemsSource = items;
            StickerList.SelectedIndex = index;
        }
    }

    // 数量输入非法时退回默认值，而不是报错打断操作。
    private int ReadStickerCount()
        => int.TryParse(StickerCountBox.Text.Trim(), out var n) && n > 0 ? Math.Min(n, 500) : 48;

    // ── 语音库 ──

    private bool _voicesLoaded;

    private async Task LoadVoicesAsync()
    {
        var result = await _client.GetVoicesAsync();

        if (result is null)
        {
            VoiceHintText.Text = "未能读取语音库（桥接可能未运行）。";
            return;
        }

        // 库为空不算失败，桥接会在 hint 里直接告诉你目录在哪。
        if (!result.Ok)
        {
            VoiceHintText.Text = $"读取失败：{result.Error ?? "（无原因）"}";
            return;
        }

        VoiceDirText.Text = string.IsNullOrWhiteSpace(result.Dir) ? "（桥接未告知）" : result.Dir!;
        VoiceList.ItemsSource = result.Voices;
        VoiceLimitsText.Text = result.Limits?.Display ?? string.Empty;

        if (result.Count == 0)
        {
            VoiceHintText.Text = result.Hint ?? "语音库是空的。";
        }
        else if (result.Voices.Any(v => !v.Ok))
        {
            // 探测没过的文件发不出去，先说一声，别等用户点了才告诉他。
            VoiceHintText.Text = $"共 {result.Count} 个文件。其中有的探测没通过，选中时会说明原因。";
        }
        else
        {
            VoiceHintText.Text = $"共 {result.Count} 个文件，都可以发。";
        }

        FillVoiceTargets();
    }

    // 目标会话下拉。用可编辑的 ComboBox：这里给的只是"建议值"（会话表里的 key），
    // 用户照样可以手写一个还没出现过但确实在白名单里的会话。
    private void FillVoiceTargets()
    {
        var typed = VoiceTargetCombo.Text;

        if (VoiceTargetCombo.ItemsSource is null)
        {
            VoiceTargetCombo.ItemsSource = _sessionsSnapshot.Select(s => s.Key).ToList();
        }

        if (!string.IsNullOrWhiteSpace(typed))
        {
            VoiceTargetCombo.Text = typed;
        }
    }

    private async void OnVoiceRefreshClick(object sender, RoutedEventArgs e)
        => await LoadVoicesAsync();

    // 选中文件就填进右边的文件名框 —— 手输路径容易写错，而这里的名字是桥接刚刚确认存在的。
    private void OnVoiceSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (VoiceList.SelectedItem is not VoiceEntry voice)
        {
            return;
        }

        VoiceFileBox.Text = voice.Name;
        VoiceResultSummary.Text = string.Empty;
        VoiceResultText.Text = string.Empty;

        if (!voice.Ok)
        {
            VoiceResultSummary.Text = $"✗ 这个文件发不出去：{voice.Error ?? "（桥接没给原因）"}";
        }
    }

    private async void OnVoiceCheckClick(object sender, RoutedEventArgs e)
        => await SendVoiceAsync(dryRun: true);

    private async void OnVoiceSendClick(object sender, RoutedEventArgs e)
    {
        if (!ReadVoiceInputs(out var target, out var file))
        {
            return;
        }

        // 语音发出去撤不回来，真发之前挡一道。
        var answer = System.Windows.MessageBox.Show(
            $"把「{file}」作为语音发到 {target}？{Environment.NewLine}{Environment.NewLine}"
            + "发出去就撤不回来了。想先确认这个文件能不能发，用「先检查」。",
            "确认发送语音",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            VoiceResultSummary.Text = "已取消，没有发出。";
            return;
        }

        await SendVoiceAsync(dryRun: false);
    }

    // 两个入口共用的输入校验。省得"先检查"和"发送"对空输入给出两套说法。
    private bool ReadVoiceInputs(out string target, out string file)
    {
        target = VoiceTargetCombo.Text.Trim();
        file = VoiceFileBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(target))
        {
            VoiceResultSummary.Text = "✗ 先填目标会话（形如 group:123456 或 private:123456）。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            VoiceResultSummary.Text = "✗ 先在左边选一个音频文件，或直接写文件名。";
            return false;
        }

        return true;
    }

    private async Task SendVoiceAsync(bool dryRun)
    {
        if (!ReadVoiceInputs(out var target, out var file))
        {
            return;
        }

        VoiceResultSummary.Text = dryRun ? "正在检查…" : "正在发送…";
        VoiceResultText.Text = string.Empty;

        var result = await _client.SendVoiceAsync(target, file, ReadVoiceVolume(), normalize: false, dryRun: dryRun);

        if (result is null)
        {
            VoiceResultSummary.Text = $"✗ {_client.LastError ?? "（没有响应）"}";
            return;
        }

        VoiceResultSummary.Text = result.Summary;
        VoiceResultText.Text = result.DetailText;
    }

    // 音量留空表示"用桥接默认"，所以空串不能当成 0。
    private double? ReadVoiceVolume()
        => double.TryParse(VoiceVolumeBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    // ── 上下文重置 ──

    // 重置选中会话的上下文：清掉它的 DSH 映射并重建，相当于让她忘掉这段对话。
    private async void OnResetSessionClick(object sender, RoutedEventArgs e)
    {
        if (SessionGrid.SelectedItem is not BridgeSessionEntry session)
        {
            ResetHintText.Text = "请先在表格里选中一个会话。";
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            $"重置「{session.Key}」的上下文？{Environment.NewLine}{Environment.NewLine}"
            + "她会忘掉这段对话的来龙去脉，正在进行的回合会被中断。此操作不可撤销。",
            "确认重置上下文",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            ResetHintText.Text = "已取消。";
            return;
        }

        var okReset = await _client.ResetSessionAsync(session.Key);
        ResetHintText.Text = okReset
            ? $"已重置「{session.Key}」的上下文。"
            : "重置失败：桥接未接受（可能该会话已不存在）。";

        if (okReset)
        {
            await ReloadAsync();
        }
    }

    // 重置全部上下文（清空所有会话映射）。
    private async void OnResetWorkspaceClick(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            "重置「全部」上下文？" + Environment.NewLine + Environment.NewLine
            + "所有群与私聊的 DSH 会话映射都会被清空，她将忘掉全部历史对话。"
            + "正在进行的回合会被中断。此操作不可撤销。",
            "确认重置全部上下文",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            ResetHintText.Text = "已取消。";
            return;
        }

        var okReset = await _client.ResetWorkspaceAsync();
        ResetHintText.Text = okReset ? "已重置全部上下文。" : "重置失败：桥接未接受。";

        if (okReset)
        {
            await ReloadAsync();
        }
    }

    // ── 挂起审批 / 提问 ──

    // 挂起项在表格里的呈现（DataGrid 绑定需要公开属性）。
    public sealed record PendingRow(string KindText, string Key, string Detail);

    // 由挂起项转成表格行。
    private static PendingRow ToRow(PendingItem item)
    {
        // PendingItem.Display 已经把"在哪、要你做什么"写全了，这里拆出两列避免重复。
        var kind = item.IsApproval ? "工具审批" : "提问";
        var detail = item.IsApproval
            ? $"执行 {item.ToolName ?? "(未知工具)"}"
              + (string.IsNullOrWhiteSpace(item.Reason) ? string.Empty : $" —— {item.Reason}")
            : string.Join(" / ", (item.Questions ?? []).Select(q => q.Question ?? "(无题面)"));

        return new PendingRow(kind, item.Key, detail);
    }

    // 读取挂起审批 / 提问。
    // 只展示不代答：桥接要求审批必须来自**管理员本人的 QQ 消息**（非管理员会被回"仅管理员可操作"），
    // 桌面端伪造不了这个身份，所以给按钮反而是骗人的。
    private async Task LoadPendingAsync()
    {
        var response = await _client.GetPendingAsync();
        if (response is null)
        {
            PendingCountText.Text = "未能读取（桥接可能未运行）";
            return;
        }

        PendingGrid.ItemsSource = response.Pending.Select(ToRow).ToList();

        PendingCountText.Text = response.Pending.Count == 0
            ? "当前没有待处理项"
            : $"有 {response.Pending.Count} 项待处理";
    }

    private async void OnRefreshPendingClick(object sender, RoutedEventArgs e) => await LoadPendingAsync();

    // ── 静默开关 ──

    // 切换静默模式。
    // 静默下不回复群友消息（仍记录），管理员私聊照常处理 —— 临时走开时用。
    private async void OnToggleSilentClick(object sender, RoutedEventArgs e)
    {
        var silently = _roleModeIsSilent;
        var target = silently ? "active" : "silent";

        var okSet = await _client.SetRoleModeAsync(target);
        if (!okSet)
        {
            SilentStateText.Text = "切换失败：桥接未接受（请确认桥接在运行）。";
            return;
        }

        _roleModeIsSilent = !silently;
        UpdateSilentUi();
        SilentStateText.Text = _roleModeIsSilent
            ? "已开启静默 —— 群友消息不再触发回复。"
            : "已恢复 —— 群消息照常处理。";
    }

    private void UpdateSilentUi()
    {
        SilentToggleButton.Content = _roleModeIsSilent ? "关闭静默" : "开启静默";
        SilentStateText.Text = _roleModeIsSilent ? "静默中" : "正常";
    }

    // ── 消耗走势 ──

    // 读取消耗走势。
    // 纯计算部分（筛选有消耗的桶、汇总、格式化）已抽到 Models.CostTrendBuilder，
    // 这里只负责取数与填表 —— 这样那段逻辑能单测，本文件也不再继续变长。
    private async Task LoadCostTrendAsync()
    {
        var series = await _client.GetTokenSeriesAsync();
        if (series is null)
        {
            CostTrendHintText.Text = "未能读取消耗走势（桥接可能未运行）。";
            return;
        }

        var result = Models.CostTrendBuilder.Build(series);
        CostTrendGrid.ItemsSource = result.Rows;
        CostTrendHintText.Text = result.Summary;
    }

    // ── 会话与上下文 ──

    // 程序化设置单选/勾选时抑制事件，避免把"载入"当成"用户改动"。
    private bool _suppressSessionUiEvents;

    // 载入会话模式与跨群读取开关。
    private async Task LoadSessionContextAsync()
    {
        var mode = await _client.GetSessionModeAsync();
        var social = await _client.GetSocialV2ConfigAsync();

        if (mode is null && social is null)
        {
            SessionHintText.Text = "未能读取会话设置（桥接可能未运行）。";
            return;
        }

        _suppressSessionUiEvents = true;
        try
        {
            var shared = string.Equals(mode?.SessionMode, "shared", StringComparison.OrdinalIgnoreCase);
            SessionModeSharedRadio.IsChecked = shared;
            SessionModePerConversationRadio.IsChecked = !shared;

            CrossGroupCheck.IsChecked = social?.Config?.CrossGroup == true;
        }
        finally
        {
            _suppressSessionUiEvents = false;
        }

        DescribeSessionState(mode);
    }

    // 把当前模式说清楚：共享模式有哪些代价、跨群读取是否开启。
    private void DescribeSessionState(SessionModeResponse? mode)
    {
        var shared = SessionModeSharedRadio.IsChecked == true;
        var cross = CrossGroupCheck.IsChecked == true;

        var parts = new List<string>
        {
            shared
                ? $"当前：共享会话（{mode?.ConversationCount ?? 0} 个会话组共用 {mode?.SessionCount ?? 0} 段上下文）"
                : $"当前：按会话分开（{mode?.SessionCount ?? 0} 段独立上下文）",
            cross ? "跨群读取：已开启" : "跨群读取：关闭",
        };

        SessionHintText.Text = string.Join(" · ", parts);
    }

    private void OnSessionModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSessionUiEvents || !IsLoaded)
        {
            return;
        }

        // 切换模式会回收并重建各会话，先把代价讲明白。
        SessionHintText.Text = SessionModeSharedRadio.IsChecked == true
            ? "待保存：将切换为共享会话。切换会回收并重建各会话，之后第一条消息会稍慢。"
            : "待保存：将切换为按会话分开。切换会回收并重建各会话。";
    }

    private async void OnSaveSessionClick(object sender, RoutedEventArgs e)
    {
        var wantShared = SessionModeSharedRadio.IsChecked == true;
        var wantMode = wantShared ? "shared" : "per-conversation";

        var current = await _client.GetSessionModeAsync();
        var modeChanged = !string.Equals(current?.SessionMode, wantMode, StringComparison.OrdinalIgnoreCase);

        var okMode = await _client.SetSessionModeAsync(wantMode);
        if (!okMode)
        {
            SessionHintText.Text = "保存失败：桥接未接受会话模式（请确认桥接在运行）。";
            return;
        }

        var okCross = await _client.SaveSocialV2ConfigAsync(new { crossGroup = CrossGroupCheck.IsChecked == true });
        if (!okCross)
        {
            SessionHintText.Text = "会话模式已保存，但跨群读取开关保存失败。";
            await LoadSessionContextAsync();
            return;
        }

        SessionHintText.Text = modeChanged
            ? "已保存。会话模式已切换，各会话会被回收重建 —— 下一条消息会稍慢。"
            : "已保存（会话模式未变，仅更新了跨群读取）。";

        await LoadSessionContextAsync();
    }

    // ── 活跃度（唤醒与发送频率）──
    //
    // 字段清单只写一份：载入、增量收集、范围校验都从这张表驱动，
    // 避免"加了个输入框但忘了接进某一边"这类只在运行时才暴露的错。

    // 字段类型。Kind 会作为 JSON 值直接提交，所以取值必须是桥接认的类型。
    private enum ActivityFieldKind
    {
        Int,
        Bool,
    }

    // 字段的作用范围。**这一页最容易误解的地方**：
    // 同样是"保存成功、回读也对"，有的字段下一刻就改变行为，有的根本碰不到已有会话。
    // 桥接侧的实现决定了这件事，界面必须说清楚，否则用户只会得出"改了不生效"。
    private enum ActivityScope
    {
        // 每次用到时现读全局配置 —— 保存后立刻生效，对已有会话一样有效。
        Global,

        // 只写进「新会话的默认唤醒配置」。已有会话各自保留自己的 wakeConfig，
        // 除非它仍是 confirmedBy === 'default'（从没被 AI 或管理员设过）。
        NewConversation,

        // 只在 AI 请求「有限时长」潜水时充当上下限。会话处于「无限期」潜水时永远用不到。
        FiniteSleepOnly,
    }

    // 一个可编辑字段：界面控件 ↔ JSON 键 ↔ 校验范围。
    // 参数 Accessor：取文本框；Kind 为 Bool 时该文本框可省略。
    // 参数 Unit：界面单位。时间类字段用 "s"（秒），桥接实际收毫秒，读写时自动换算 ——
    //   让用户填 300 而不是 300000，这是这一页最主要的易用性问题。
    // 参数 Scope：见 ActivityScope，决定保存后这句提示怎么写。
    private sealed record ActivityField(
        Func<TextBox> Accessor,
        string JsonKey,
        string Label,
        int Min,
        int Max,
        string Kind,
        string Unit = "",
        CheckBox? Toggle = null,
        ActivityScope Scope = ActivityScope.Global)
    {
        // 这个字段是否对"已经设过唤醒配置的会话"无效。
        public bool NeedsScopeWarning => Scope != ActivityScope.Global;

        // 范围提示后面缀上的作用域说明。
        public string ScopeNote => Scope switch
        {
            ActivityScope.NewConversation => "　·　只影响新会话",
            ActivityScope.FiniteSleepOnly => "　·　仅「有限时长」潜水时生效",
            _ => string.Empty,
        };
        // 输入框里的数字所允许的范围（秒类字段的边界要换算回显示单位）。
        public int DisplayMin => Unit == "s" ? Min / 1000 : Min;

        public int DisplayMax => Unit == "s" ? Max / 1000 : Max;

        // 行尾的范围提示。
        public string RangeHint
        {
            get
            {
                if (Kind == "Bool")
                {
                    return string.Empty;
                }

                var unit = Models.ActivityUnitConverter.UnitLabel(Unit);
                var extra = JsonKey == "sleepMaxMs" ? "（0 = 不限制）" : string.Empty;
                return $"{unit} · {DisplayMin} ~ {DisplayMax}{extra}{ScopeNote}";
            }
        }

        // 越界时的说明。
        public string RangeError =>
            $"「{Label}」应在 {DisplayMin} ~ {DisplayMax} 之间（{Models.ActivityUnitConverter.UnitLabel(Unit)}）";
    }

    private List<ActivityField> ActivityFields() =>
    [
        // 时间类用秒（桥接收毫秒，读写自动换算），其余为次数或毫秒。
        // 作用域依据桥接源码（D:\qq-bridge\src\bridge.js）：
        //   · sleepMinMs/sleepMaxMs  —— L5466-5469 现读全局，但只在 AI 请求有限时长时钳制
        //   · batchWindowMs          —— L10875 用的是 st.wakeConfig.batchWindowMs（会话自己的），
        //                               全局值只在 L8662 的 defaultWakeConfigV2() 里当新会话默认值
        //   · 其余字段都是调用点现读 cfg.socialV2.*，保存即生效
        new(() => SleepMinBox, "sleepMinMs", "最短潜水", 0, 86_400_000, "Int", "s", null, ActivityScope.FiniteSleepOnly),
        new(() => SleepMaxBox, "sleepMaxMs", "最长潜水", 0, 86_400_000, "Int", "s", null, ActivityScope.FiniteSleepOnly),
        new(() => MaxWakeHourBox, "maxWakePerHour", "每小时最多唤醒", 0, 1000, "Int"),
        new(() => MaxWakeMinuteBox, "maxWakePerMinute", "每分钟最多唤醒", 0, 100, "Int"),
        new(() => PreSleepWaitBox, "preSleepWaitMs", "睡前观察窗口", 0, 3_600_000, "Int", "s"),
        new(() => BatchWindowBox, "batchWindowMs", "多条消息合并窗口", 1_000, 600_000, "Int", "s", null, ActivityScope.NewConversation),
        new(() => MaxMessageCharsBox, "maxMessageChars", "单条消息最大字数", 1, 5000, "Int"),
        new(() => MaxSendHourBox, "maxSendPerHour", "每小时最多发送", 0, 10_000, "Int"),
        new(() => MaxSendMinuteBox, "maxSendPerMinute", "每分钟最多发送", 0, 1000, "Int"),
        new(() => BurstMinBox, "burstIntervalMinMs", "连发间隔下限", 0, 600_000, "Int", "ms"),
        new(() => BurstMaxBox, "burstIntervalMaxMs", "连发间隔上限", 0, 600_000, "Int", "ms"),
        new(() => GapBaseBox, "gapBaseMs", "基础间隔", 0, 600_000, "Int", "ms"),
        new(() => GapPerCharBox, "gapPerCharMs", "每字追加", 0, 1000, "Int", "ms"),
        new(() => AutoReplyCheckBox, "autoReplyCheckMs", "检查可回消息的间隔", 1_000, 600_000, "Int", "s"),
        new(() => new TextBox(), "burstEnabled", "连发开关", 0, 0, "Bool", "", BurstEnabledCheck),
    ];

    // 按 JSON 键取值（用于与基线比对，判断哪些字段真的被改动过）。
    private static string? ReadActivityValue(ActivityConfig config, string jsonKey) => jsonKey switch
    {
        "sleepMinMs" => config.SleepMinMs.ToString(),
        "sleepMaxMs" => config.SleepMaxMs.ToString(),
        "maxWakePerHour" => config.MaxWakePerHour.ToString(),
        "maxWakePerMinute" => config.MaxWakePerMinute.ToString(),
        "preSleepWaitMs" => config.PreSleepWaitMs.ToString(),
        "batchWindowMs" => config.BatchWindowMs.ToString(),
        "maxMessageChars" => config.MaxMessageChars.ToString(),
        "maxSendPerHour" => config.MaxSendPerHour.ToString(),
        "maxSendPerMinute" => config.MaxSendPerMinute.ToString(),
        "burstIntervalMinMs" => config.BurstIntervalMinMs.ToString(),
        "burstIntervalMaxMs" => config.BurstIntervalMaxMs.ToString(),
        "gapBaseMs" => config.GapBaseMs.ToString(),
        "gapPerCharMs" => config.GapPerCharMs.ToString(),
        "autoReplyCheckMs" => config.AutoReplyCheckMs.ToString(),
        "burstEnabled" => config.BurstEnabled ? "true" : "false",
        _ => null,
    };

    // 载入活跃度配置。
    private async Task LoadActivityAsync()
    {
        var config = await _client.GetActivityConfigAsync();
        if (config is null)
        {
            ActivityHintText.Text = "未能读取活跃度配置（桥接可能未运行）。";
            return;
        }

        _activityBaseline = config;

        foreach (var field in ActivityFields())
        {
            if (field.Kind == "Bool")
            {
                if (field.Toggle is not null)
                {
                    field.Toggle.IsChecked = config.BurstEnabled;
                }

                continue;
            }

            var raw = ReadActivityValue(config, field.JsonKey) ?? string.Empty;
            field.Accessor().Text = Models.ActivityUnitConverter.ToDisplay(raw, field.Unit);
        }

        UpdateActivityValidation();

        var fmt = static (int ms) => ms <= 0 ? "不限" : $"{ms / 1000.0:0.#} 秒";
        RecommendedSleepText.Text =
            $"桥接推荐：潜水 {fmt(config.RecommendedSleepMinMs)} ~ {fmt(config.RecommendedSleepMaxMs)}；"
            + "潜水时长过短会显得一直在刷屏，过长则错过话题。";

        ActivityHintText.Text = $"已载入（当前 {MatchActivityPreset(config) ?? "自定义"}）。修改后点「保存改动」，只提交改动过的项。";
    }

    // 与给定配置完全一致的预设名；都不匹配时返回 null（即"自定义"）。
    // 这里比对的是桥接原始值（毫秒），界面上的匹配判断走 MatchActivityPresetCurrent。
    // 注意：元组不支持 ?. 取成员，所以先投影出名字再 FirstOrDefault。
    private static string? MatchActivityPreset(ActivityConfig config)
        => Models.ActivityPresets.All
            .Where(p => p.Values.All(v => ReadActivityValue(config, v.Key) == v.Value.ToString()))
            .Select(p => p.Name)
            .FirstOrDefault();

    private async void OnReloadActivityClick(object sender, RoutedEventArgs e) => await LoadActivityAsync();

    // 输入框变化：把该字段标红／恢复，并给出总体提示。
    // 只做即时反馈，真正的拦截仍在保存时（避免边打字边弹错误）。
    private void OnActivityFieldChanged(object sender, RoutedEventArgs e) => UpdateActivityValidation();

    private void UpdateActivityValidation()
    {
        if (_activityBaseline is null || !IsLoaded)
        {
            return;
        }

        var invalid = 0;

        foreach (var field in ActivityFields())
        {
            if (field.Kind == "Bool")
            {
                continue;
            }

            var box = field.Accessor();
            var text = box.Text.Trim();

            var isBad = text.Length == 0
                || !Models.ActivityUnitConverter.TryToBridgeValue(text, field.Unit, out var value)
                || value < field.Min
                || value > field.Max;

            box.Foreground = isBad ? InvalidBrush : NormalBrush;
            box.ToolTip = isBad ? field.RangeError : field.RangeHint;

            if (isBad)
            {
                invalid++;
            }
        }

        if (invalid > 0)
        {
            ActivityHintText.Text = $"有 {invalid} 项填写不合法（已标红，鼠标悬停可看范围）。";
            return;
        }

        var changed = CountActivityChanges();
        var preset = MatchActivityPresetCurrent();
        ActivityHintText.Text = changed == 0
            ? $"当前配置与预设「{preset ?? "自定义"}」一致，未检测到改动。"
            : $"有 {changed} 项待保存（当前 {preset ?? "自定义"}）。";
    }

    // 统计相对基线改动了几项（不提交）。
    private int CountActivityChanges()
    {
        if (_activityBaseline is not { } baseline)
        {
            return 0;
        }

        var n = 0;
        foreach (var field in ActivityFields())
        {
            if (field.Kind == "Bool")
            {
                if (field.Toggle is not null && (field.Toggle.IsChecked == true) != baseline.BurstEnabled)
                {
                    n++;
                }

                continue;
            }

            var text = field.Accessor().Text.Trim();
            var baselineText = Models.ActivityUnitConverter.ToDisplay(ReadActivityValue(baseline, field.JsonKey) ?? string.Empty, field.Unit);
            if (text != baselineText)
            {
                n++;
            }
        }

        return n;
    }

    // 用当前界面上的值判断匹配哪一档预设（保存前也能提示）。
    // 说明：这里刻意不用桥接返回的原始值 —— 界面单位是秒，直接比对会永远不匹配。
    private string? MatchActivityPresetCurrent()
    {
        foreach (var preset in Models.ActivityPresets.All)
        {
            var allMatch = true;
            foreach (var (key, value) in preset.Values)
            {
                var field = ActivityFields().FirstOrDefault(f => f.JsonKey == key);
                if (field is null)
                {
                    allMatch = false;
                    break;
                }

                var shown = Models.ActivityUnitConverter.ToDisplay(value.ToString(), field.Unit);
                if (field.Accessor().Text.Trim() != shown)
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                return preset.Name;
            }
        }

        return null;
    }

    // 输入合法／非法的前景色。用画刷资源而不是硬编码颜色，跟随主题。
    private static readonly System.Windows.Media.Brush NormalBrush =
        System.Windows.Media.Brushes.Black;

    private static readonly System.Windows.Media.Brush InvalidBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD1, 0x34, 0x38));

    // 占用条的正常色。用固定值而不是查 MahApps 的资源键 ——
    // 键名一旦变化，Resources[...] 会返回 null 并在运行时抛空引用，而这里只是显示逻辑。
    private static readonly System.Windows.Media.Brush AccentBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));

    // 收集并保存改动。返回是否保存成功（部分失败时把桥接原话显示出来）。
    // 参数 batchLabel：整组应用时的说明（预设名），用于把成功提示写得更明确。
    private async Task<bool> SaveActivityAsync(string? batchLabel = null)
    {
        if (_activityBaseline is not { } baseline)
        {
            ActivityHintText.Text = "请先点「重新载入」取得当前配置，再修改保存。";
            return false;
        }

        var changes = new Dictionary<string, object>();

        foreach (var field in ActivityFields())
        {
            if (field.Kind == "Bool")
            {
                if (field.Toggle is null)
                {
                    continue;
                }

                var want = field.Toggle.IsChecked == true;
                if (want != baseline.BurstEnabled)
                {
                    changes[field.JsonKey] = want;
                }

                continue;
            }

            var text = field.Accessor().Text.Trim();
            var baselineText = Models.ActivityUnitConverter.ToDisplay(ReadActivityValue(baseline, field.JsonKey) ?? string.Empty, field.Unit);
            if (text == baselineText)
            {
                continue;   // 没动过就不提交，减少误改面
            }

            if (!Models.ActivityUnitConverter.TryToBridgeValue(text, field.Unit, out var number))
            {
                ActivityHintText.Text = $"「{field.Label}」必须是数字（不能留空，也不要写单位）。";
                return false;
            }

            if (number < field.Min || number > field.Max)
            {
                ActivityHintText.Text = field.RangeError;
                return false;
            }

            changes[field.JsonKey] = number;
        }

        if (changes.Count == 0)
        {
            ActivityHintText.Text = "没有检测到改动。";
            return false;
        }

        var error = await _client.SaveActivityAsync(changes);
        if (error is not null)
        {
            ActivityHintText.Text = $"保存失败：{error}";
            return false;
        }

        var presetName = MatchActivityPreset(await _client.GetActivityConfigAsync() ?? baseline);

        // 改了「只影响新会话」或「仅有限时长潜水时生效」的字段时，必须说清楚，
        // 否则用户看到"已保存"却观察不到任何变化 —— 这正是这一页最初被反馈"改了不生效"的原因。
        var limited = ActivityFields()
            .Where(f => changes.ContainsKey(f.JsonKey) && f.NeedsScopeWarning)
            .Select(f => $"「{f.Label}」")
            .ToList();
        var scopeNote = limited.Count == 0
            ? string.Empty
            : $"　注意：{string.Join("、", limited)} 对已经设过唤醒配置的会话不起作用"
              + "（每个会话各自保留自己的配置，可在「二代仿真」页里逐会话改）。";

        await LoadActivityAsync();   // 取回桥接侧的最终值，顺带清掉改动集

        // LoadActivityAsync 会把提示改成"已载入"，这里补回本次操作的结果。
        ActivityHintText.Text = batchLabel is null
            ? $"已保存 {changes.Count} 项（即时生效；当前 {presetName ?? "自定义"}）。{scopeNote}"
            : $"已应用预设「{batchLabel}」，共写入 {changes.Count} 项。{scopeNote}";

        return true;
    }

    private async void OnSaveActivityClick(object sender, RoutedEventArgs e) => await SaveActivityAsync();


    private async void OnPresetHighClick(object sender, RoutedEventArgs e) => await ApplyPresetAsync(0);

    private async void OnPresetNormalClick(object sender, RoutedEventArgs e) => await ApplyPresetAsync(1);

    private async void OnPresetLowClick(object sender, RoutedEventArgs e) => await ApplyPresetAsync(2);

    // 应用一档预设：先写进输入框（让用户看到改了什么），再走与手动保存相同的增量提交路径。
    private async Task ApplyPresetAsync(int index)
    {
        if (_activityBaseline is null)
        {
            ActivityHintText.Text = "请先点「重新载入」取得当前配置，再套用预设。";
            return;
        }

        var preset = Models.ActivityPresets.All[index];

        var answer = System.Windows.MessageBox.Show(
            $"套用「{preset.Name}」预设？\n\n{preset.Description}\n\n"
            + $"即将写入 {preset.Values.Count} 项参数，会覆盖这些项当前的值。",
            "确认套用预设",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            ActivityHintText.Text = "已取消。";
            return;
        }

        // 先落到输入框：失败时用户也能直接看到被改成了什么。
        foreach (var field in ActivityFields())
        {
            if (field.Kind == "Bool" || !preset.Values.TryGetValue(field.JsonKey, out var value))
            {
                continue;
            }

            // value 是毫秒（桥接单位），秒类字段要换算成秒再显示。
            field.Accessor().Text = Models.ActivityUnitConverter.ToDisplay(value.ToString(), field.Unit);
        }

        UpdateActivityValidation();

        await SaveActivityAsync(preset.Name);
    }
}
