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

/// <summary>
/// 桥接控制台页：模式、白名单、人设、配额、令牌与花费。
/// </summary>
/// <remarks>
/// 全部通过桥接的 HTTP API 完成，不内嵌网页。
/// 保存类操作即时生效；改变端口等配置项需要重启桥接（页面上另有提示）。
/// </remarks>
public partial class BridgeConsoleView : UserControl
{
    /// <summary>可选运行模式（与桥接 /api/mode 接受的值一致）。</summary>
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

    /// <summary>最近一次会话快照（供状态应用时复用，避免重复请求）。</summary>
    private IReadOnlyList<BridgeSessionEntry> _sessionsSnapshot = [];

    /// <summary>初始化页面。</summary>
    /// <param name="services">应用服务宿主。</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 <c>null</c>。</exception>
    public BridgeConsoleView(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _client = new BridgeApiClient(_services.Endpoints.BridgeConsole);

        InitializeComponent();

        ModeCombo.ItemsSource = Modes.Select(m => m.Label).ToList();
        GroupList.ItemsSource = _groups;
        SessionGrid.ItemsSource = _sessions;

        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>群列表项：包一层是为了给多选状态与显示文本留位置。</summary>
    /// <param name="Id">群号。</param>
    /// <param name="Name">群名。</param>
    public sealed record GroupItem(string Id, string Name)
    {
        /// <summary>界面显示文本。</summary>
        public string Display => $"{Name}（{Id}）";
    }

    /// <summary>重新载入全部数据。</summary>
    private async void OnReloadClick(object sender, RoutedEventArgs e) => await ReloadAsync();

    /// <summary>拉取桥接的各类配置与状态。</summary>
    private async Task ReloadAsync()
    {
        // 配置可能在设置页被改过（端口变化），因此每次重新载入都重建客户端。
        _client.Dispose();
        _client = new BridgeApiClient(_services.Endpoints.BridgeConsole);

        // 每次重载前清空旧数据，避免叠加。
        StatusText.Text = "正在载入…";

        var status = await _client.GetStatusAsync();
        if (status is null)
        {
            StatusText.Text = $"无法读取桥接（{_services.Endpoints.BridgeConsole}）—— 服务未启动或端口不对。";
            HintText.Text = "提示：可在「服务管理」页启动桥接，或在「设置」页修正端口。";
            return;
        }

        ApplyStatus(status);
        await LoadWhitelistAsync();
        await LoadGroupsAsync();
        await LoadPersonaAsync();
        await LoadQuotaAsync();
        await LoadTokensAsync();

        StatusText.Text = $"已载入（{DateTime.Now:HH:mm:ss}）";
        HintText.Text = $"桥接地址：{_services.Endpoints.BridgeConsole}";
    }

    /// <summary>应用运行状态：模式、会话、模型。</summary>
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

    /// <summary>载入白名单。</summary>
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

    /// <summary>载入群列表并按当前白名单勾选。</summary>
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

    /// <summary>载入人设。</summary>
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

    /// <summary>载入配额。</summary>
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
        TodayUsageText.Text = $"群 {quota.TodayGroup} 条 / 私聊 {quota.TodayPrivate} 条";
    }

    /// <summary>载入 token 汇总与最近活动。</summary>
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
        // 模型与推理强度：可选档位由桥接按 DSH 实际公布的能力下发。
        var selection = await _client.GetModelSelectionAsync();
        if (selection is not null)
        {
            var options = selection.Options.Count > 0 ? selection.Options : new List<string> { "low", "high", "max" };
            EffortCombo.ItemsSource = options;
            var current = ChooseEffort(selection.ReasoningEffort);
            EffortCombo.SelectedItem = options.FirstOrDefault(o => string.Equals(o, current, StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault();
            ModelText.Text += $" / 模型 {selection.Provider}/{selection.Model}";
        }
    }

    /// <summary>切换运行模式。</summary>
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

    /// <summary>保存推理强度。</summary>
    /// <summary>保存推理强度（写入 DSH 全局默认，对已有会话于下一条消息生效）。</summary>
    private async void OnApplyEffortClick(object sender, RoutedEventArgs e)
    {
        var effort = ChooseEffort(EffortCombo.SelectedItem?.ToString());
        if (effort is null)
        {
            StatusText.Text = "请先选择推理强度。";
            return;
        }

        var ok = await _client.SetReasoningEffortAsync(effort);
        StatusText.Text = ok
            ? $"推理强度已设为 {effort}（下一条消息生效；同时写入 DSH 全局默认）"
            : "设置推理强度失败：请检查桥接是否在运行。";
    }

    /// <summary>把界面文本映射为可保存的强度档位；非法输入返回 null。</summary>
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

    /// <summary>保存白名单。</summary>
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

    /// <summary>保存人设。</summary>
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

    /// <summary>保存配额。</summary>
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

        var quota = new QuotaConfig
        {
            Enabled = QuotaEnabledCheck.IsChecked == true,
            GroupWarnAt = groupWarn,
            GroupLimit = groupLimit,
            PrivateWarnAt = privateWarn,
            PrivateLimit = privateLimit,
            ExemptOwners = SplitLines(ExemptBox.Text),
            WarnText = WarnTextBox.Text,
        };

        var ok = await _client.SaveQuotaAsync(quota);
        QuotaHintText.Text = ok ? "已保存（即时生效）" : "保存失败：请检查桥接是否在运行。";
    }

    /// <summary>把多行文本拆成去空的列表。</summary>
    private static List<string> SplitLines(string text) => text
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>宽松的整数解析：失败返回 false 而不抛异常。</summary>
    private static bool TryParseInt(string text, out int value)
        => int.TryParse(text.Trim(), out value);
}
