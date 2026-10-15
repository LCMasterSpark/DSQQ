using System.Collections.ObjectModel;
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

            StatusText.Text = $"桥接尚未就绪，正在重试（{attempt}/{Attempts - 1}）…";
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
            StatusText.Text = $"无法读取桥接（{_services.Endpoints.BridgeConsole}）—— 服务未启动或端口不对。";
            HintText.Text = "提示：可在「服务管理」页启动桥接，或在「设置」页修正端口；桥接就绪后点「重新载入」即可。";
            return;
        }

        ApplyStatus(status);
        await LoadWhitelistAsync();
        await LoadGroupsAsync();
        await LoadPersonaAsync();
        await LoadQuotaAsync();
        await LoadActivityAsync();
        await LoadTokensAsync();
        await LoadRolesAsync();
        await LoadSlangAsync();

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
        ModelText.Text = $"DSH 就绪 {status.DshReady} / SnowLuma 连接 {snowLuma?.Connected} / 接收健康 {snowLuma?.Good}";
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
            var totals = summary.Totals;
            TokenSummaryText.Text =
                $"计价档位 : {summary.NowTier?.TierLabel ?? "未知"}（{summary.NowTier?.Label ?? "?"}）{Environment.NewLine}"
                + $"输入命中 : {totals?.CacheHit ?? 0:N0}{Environment.NewLine}"
                + $"输入未命中: {totals?.CacheMiss ?? 0:N0}{Environment.NewLine}"
                + $"输出     : {totals?.Output ?? 0:N0}{Environment.NewLine}"
                + $"合计     : {totals?.Total ?? 0:N0} tokens{Environment.NewLine}"
                + $"折算金额 : {summary.Symbol}{summary.Cost?.Total ?? 0:0.####}";
        }

        var status = await _client.GetStatusAsync();
        ActivityText.Text = string.IsNullOrWhiteSpace(status?.Activity) ? "（暂无活动）" : status.Activity;

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
        RoleStatusText.Text = ok ? $"已启用「{summary.Name}」（下一条消息生效）" : "启用失败。";
    }

    // 清除当前角色。
    private async void OnRoleClearClick(object sender, RoutedEventArgs e)
    {
        var ok = await _client.SetCurrentRoleAsync(string.Empty);
        RoleStatusText.Text = ok ? "已清除角色（恢复为纯仿真提示词）。" : "清除失败。";
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

    private async void OnPresetLoadClick(object sender, RoutedEventArgs e)
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
        PresetStatusText.Text = ok
            ? $"已保存 {preset}；需重启 DSH 生效。"
            : "保存被拒绝：可能缺少安全不变量（权限边界、令牌、角色注入等）或正文过短。";
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

    // 一个可编辑字段：界面控件 ↔ JSON 键 ↔ 校验范围。
    // 参数 Accessor：取文本框；Kind 为 Bool 时该文本框可省略。
    private sealed record ActivityField(
        Func<TextBox> Accessor,
        string JsonKey,
        string Label,
        int Min,
        int Max,
        string Kind,
        CheckBox? Toggle = null);

    private List<ActivityField> ActivityFields() =>
    [
        new(() => SleepMinBox, "sleepMinMs", "最短潜水", 0, 86400000, "Int"),
        new(() => SleepMaxBox, "sleepMaxMs", "最长潜水", 0, 86400000, "Int"),
        new(() => MaxWakeHourBox, "maxWakePerHour", "每小时最多唤醒", 0, 1000, "Int"),
        new(() => MaxWakeMinuteBox, "maxWakePerMinute", "每分钟最多唤醒", 0, 100, "Int"),
        new(() => PreSleepWaitBox, "preSleepWaitMs", "睡前观察窗口", 0, 3600000, "Int"),
        new(() => BatchWindowBox, "batchWindowMs", "多条消息合并窗口", 1000, 600000, "Int"),
        new(() => MaxMessageCharsBox, "maxMessageChars", "单条消息最大字数", 1, 5000, "Int"),
        new(() => MaxSendHourBox, "maxSendPerHour", "每小时最多发送", 0, 10000, "Int"),
        new(() => MaxSendMinuteBox, "maxSendPerMinute", "每分钟最多发送", 0, 1000, "Int"),
        new(() => BurstMinBox, "burstIntervalMinMs", "连发间隔下限", 0, 600000, "Int"),
        new(() => BurstMaxBox, "burstIntervalMaxMs", "连发间隔上限", 0, 600000, "Int"),
        new(() => GapBaseBox, "gapBaseMs", "基础间隔", 0, 600000, "Int"),
        new(() => GapPerCharBox, "gapPerCharMs", "每字追加", 0, 1000, "Int"),
        new(() => AutoReplyCheckBox, "autoReplyCheckMs", "回复检查间隔", 1000, 600000, "Int"),
        new(() => new TextBox(), "burstEnabled", "连发开关", 0, 0, "Bool", BurstEnabledCheck),
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

            field.Accessor().Text = ReadActivityValue(config, field.JsonKey) ?? string.Empty;
        }

        var fmt = static (int ms) => ms <= 0 ? "不限" : $"{ms / 1000.0:0.#} 秒";
        RecommendedSleepText.Text =
            $"桥接推荐：潜水 {fmt(config.RecommendedSleepMinMs)} ~ {fmt(config.RecommendedSleepMaxMs)}；"
            + "潜水时长过短会显得一直在刷屏，过长则错过话题。";

        ActivityHintText.Text = $"已载入（当前 {MatchActivityPreset(config) ?? "自定义"}）。修改后点「保存改动」，只提交改动过的项。";
    }

    // 与当前配置完全一致的预设名；都不匹配时返回 null（即"自定义"）。
    // 注意：元组不支持 ?. 取成员，所以先投影出名字再 FirstOrDefault。
    private static string? MatchActivityPreset(ActivityConfig config)
        => Models.ActivityPresets.All
            .Where(p => p.Values.All(v => ReadActivityValue(config, v.Key) == v.Value.ToString()))
            .Select(p => p.Name)
            .FirstOrDefault();

    private async void OnReloadActivityClick(object sender, RoutedEventArgs e) => await LoadActivityAsync();

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
            var baselineText = ReadActivityValue(baseline, field.JsonKey) ?? string.Empty;
            if (text == baselineText)
            {
                continue;   // 没动过就不提交，减少误改面
            }

            if (!int.TryParse(text, out var number))
            {
                ActivityHintText.Text = $"「{field.Label}」必须是整数（不能留空，也不要写单位）。";
                return false;
            }

            if (number < field.Min || number > field.Max)
            {
                ActivityHintText.Text = $"「{field.Label}」必须在 {field.Min} ~ {field.Max} 之间。";
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

        var changedKeys = new HashSet<string>(changes.Keys, StringComparer.Ordinal);
        var presetName = MatchActivityPreset(await _client.GetActivityConfigAsync() ?? baseline);

        ActivityHintText.Text = batchLabel is null
            ? $"已保存 {changes.Count} 项（即时生效）。"
            : $"已应用预设「{batchLabel}」，共写入 {changes.Count} 项。";

        await LoadActivityAsync();   // 取回桥接侧的最终值，顺带清掉改动集

        // LoadActivityAsync 会把提示改成"已载入"，这里补回本次操作的结果。
        ActivityHintText.Text = batchLabel is null
            ? $"已保存 {changes.Count} 项（即时生效；当前 {presetName ?? "自定义"}）。"
            : $"已应用预设「{batchLabel}」，共写入 {changes.Count} 项。";

        _ = changedKeys;
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

            field.Accessor().Text = value.ToString();
        }

        await SaveActivityAsync(preset.Name);
    }
}
