using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using DSQQ.Models;
using DSQQ.Services;
using UserControl = System.Windows.Controls.UserControl;
using PasswordBox = System.Windows.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;
using DataGrid = System.Windows.Controls.DataGrid;
using Button = System.Windows.Controls.Button;
using Timer = System.Timers.Timer;

namespace DSQQ.Views;

// SnowLuma 控制台。
//
// 目标：日常不必再打开 SnowLuma 的网页。分三个分页 ——
//   概览       版本、账号、WebUI 登录态、接入点连接情况
//   QQ 账号    进程与注入状态、加载/卸载/重新探测
//   OneBot 配置 HTTP / WS 服务的读取与写入
//
// 不做的事：协议同意与 QQ 扫码。那是 QQ 客户端的登录流程，
// 本程序只能把你送到 SnowLuma 界面，不能代做。
public partial class SnowLumaView : UserControl
{
    private readonly AppServices _services;
    private readonly ObservableCollection<SnowLumaProcess> _processes = [];
    private Timer? _timer;
    private SnowLumaApiClient _client;

    // 当前正在编辑的 OneBot 配置（整份持有，保存时整体回写）。
    private SnowLumaOneBotConfig? _config;
    private string? _configUin;

    // 界面是否正在回填控件 —— 回填时不要触发"用户改动了"的副作用。
    private bool _loading;

    // 更新检查拿到的发布页地址（用于「打开发布页」）。
    private string? _releaseUrl;

    public SnowLumaView(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _client = CreateClient();

        InitializeComponent();
        ProcessGrid.ItemsSource = _processes;

        // 消息格式与 WS 角色的可选项。用固定集合而不是自由文本：
        // 写错一个字母 SnowLuma 会接受、但桥接就解析不了消息。
        HttpFormatCombo.ItemsSource = new[] { "array", "string" };
        WsRoleCombo.ItemsSource = new[] { "Universal", "Api", "Event" };

        Loaded += async (_, _) =>
        {
            RestoreSavedCredentials();
            StartTimer();

            // 有保存的密码却没有有效会话时，自动登录一次。
            // 少了这一步，用户每次开 DSQQ 都得手点一次登录 ——
            // 而保存凭据的意义正是免去这一下。
            await TryAutoLoginAsync();

            await RefreshAsync();
        };

        Unloaded += (_, _) => StopTimer();
    }

    // 按当前配置创建客户端（配置变更后需重建）。
    private SnowLumaApiClient CreateClient()
    {
        var client = new SnowLumaApiClient(_services.Endpoints.SnowLumaWebUi);
        var token = SecureStore.Unprotect(_services.Config.SnowLuma.SessionTokenProtected);
        if (!string.IsNullOrEmpty(token))
        {
            client.ImportToken(token);
        }

        return client;
    }

    // 把保存的密码填回输入框，省去重复输入。
    private void RestoreSavedCredentials()
    {
        var saved = SecureStore.Unprotect(_services.Config.SnowLuma.WebUiPasswordProtected);
        if (!string.IsNullOrEmpty(saved))
        {
            PasswordBox.Password = saved;
            LoginStatusText.Text = $"已载入保存的凭据，正在尝试恢复登录（{_services.Endpoints.SnowLumaWebUi}）。";
        }
        else
        {
            LoginStatusText.Text = $"尚未保存凭据。WebUI 地址：{_services.Endpoints.SnowLumaWebUi}";
        }
    }

    // 用保存的凭据自动登录。
    //
    // 只在"有密码、但当前没有有效会话"时尝试 —— 会话还有效就不必多打一次登录接口
    // （SnowLuma 对登录有频率限制，无谓的尝试会把自己挡在门外）。
    private async Task TryAutoLoginAsync()
    {
        if (_client.HasSession)
        {
            LoginStatusText.Text = "会话仍然有效，已自动恢复登录。";
            return;
        }

        var saved = SecureStore.Unprotect(_services.Config.SnowLuma.WebUiPasswordProtected);
        if (string.IsNullOrEmpty(saved))
        {
            return;
        }

        LoginStatusText.Text = "正在用保存的凭据自动登录…";

        var result = await _client.LoginAsync(saved);

        switch (result.Outcome)
        {
            case LoginOutcome.Success:
                SaveCredentials(saved);
                LoginStatusText.Text = "已用保存的凭据自动登录。";
                break;

            case LoginOutcome.NeedsTotp:
                LoginStatusText.Text = "该账号启用了两步验证：请在「验证码」框填写动态码后点登录。";
                break;

            case LoginOutcome.RateLimited:
                LoginStatusText.Text = $"自动登录被限流：{result.Message}";
                break;

            case LoginOutcome.Unreachable:
                LoginStatusText.Text = $"未连上 SnowLuma（{_services.Endpoints.SnowLumaWebUi}）—— 它可能还没启动。";
                break;

            default:
                // 密码多半已经被改过；把框留着让用户改，不要清掉。
                LoginStatusText.Text = $"自动登录失败（{result.Message}）—— 请在上方重新输入密码。";
                break;
        }
    }

    // 每 5 秒刷新一次状态（进程与账号会随扫码登录变化）。
    private void StartTimer()
    {
        _timer = new Timer(5000) { AutoReset = true };
        // System.Timers.Timer 的回调在线程池线程上执行，界面更新必须切回 UI 线程；
        // 任务本身的异常交给 TaskObserver 观察，避免 DispatcherOperation 吞掉异常。
        _timer.Elapsed += (_, _) => Dispatcher.InvokeAsync(
            () => TaskObserver.FireAndForget(RefreshAsync, "SnowLuma 页定时刷新"));
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    // ── 登录 ──

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        try
        {
            var password = PasswordBox.Password;
            var totp = TotpBox.Text.Trim();

            var result = await _client.LoginAsync(password, string.IsNullOrEmpty(totp) ? null : totp);

            switch (result.Outcome)
            {
                case LoginOutcome.Success:
                    SaveCredentials(password);
                    LoginStatusText.Text = "登录成功，会话已保存。";

                    // 登录后立刻刷新一次，避免用户等待下一个轮询周期。
                    await RefreshAsync();
                    break;

                case LoginOutcome.NeedsTotp:
                    LoginStatusText.Text = "该账号启用了两步验证：请在「验证码」框填写动态码后重试。";
                    break;

                case LoginOutcome.RateLimited:
                    LoginStatusText.Text = $"登录被限流：{result.Message}";
                    break;

                default:
                    LoginStatusText.Text = $"登录失败：{result.Message}";
                    break;
            }
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        await _client.LogoutAsync();

        var config = _services.Config;
        config.SnowLuma.SessionTokenProtected = null;
        config.SnowLuma.WebUiPasswordProtected = null;
        TrySaveConfig();

        _client = CreateClient();
        LoginStatusText.Text = "已退出登录，并清除本地保存的凭据。";
        _processes.Clear();
        OverviewText.Text = "尚未采集。";
        ConnectionsText.Text = "（未采集）";
    }

    // 保存凭据（DPAPI 加密）。
    private void SaveCredentials(string password)
    {
        var config = _services.Config;
        config.SnowLuma.WebUiPasswordProtected = SecureStore.Protect(password);

        var token = _client.ExportToken();
        config.SnowLuma.SessionTokenProtected = string.IsNullOrEmpty(token) ? null : SecureStore.Protect(token);

        TrySaveConfig();
    }

    private void TrySaveConfig()
    {
        try
        {
            _services.ApplyConfig(_services.Config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoginStatusText.Text = $"凭据保存失败（本次登录仍然有效）：{ex.Message}";
        }
    }

    // ── 刷新 ──

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        var status = await _client.GetStatusAsync();
        var processes = await _client.GetProcessesAsync();

        // 保留用户在表格里的选中项：定时刷新每 5 秒一次，
        // 每次都清空重填会让"选中某一行再点加载"变得几乎不可能。
        var selectedPid = (ProcessGrid.SelectedItem as SnowLumaProcess)?.Pid;

        _processes.Clear();
        foreach (var process in processes)
        {
            _processes.Add(process);
        }

        if (selectedPid is not null)
        {
            ProcessGrid.SelectedItem = _processes.FirstOrDefault(p => p.Pid == selectedPid);
        }

        if (status is null)
        {
            OverviewText.Text = _client.HasSession
                ? $"无法读取状态：{_services.Endpoints.SnowLumaWebUi} 未响应或会话已失效。"
                : "未登录。请在上方输入 WebUI 密码后登录。";
        }
        else
        {
            // 账号信息取自 /api/qq-list —— /api/status 只回 {"status":"running"}，
            // 里面既没有版本也没有账号（早先按 {version,accounts} 建模，两个字段永远为空）。
            var accounts = await _client.GetQqListAsync();
            var accountText = accounts.Count == 0
                ? "（无已登录账号）"
                : string.Join("、", accounts.Select(a => $"{a.Nickname ?? "?"}({a.Uin ?? "?"})"));

            // 曾经这里读的是 p.Loaded，而真实字段叫 injected —— 那个属性恒为 false，
            // 所以「已注入 N 个」一直显示 0，把排查引向错误方向。
            var injected = processes.Count(p => p.Injected);
            var loggedIn = processes.Count(p => p.LoggedIn);

            OverviewText.Text =
                $"地址        : {_services.Endpoints.SnowLumaWebUi}{Environment.NewLine}"
                + $"运行状态    : {status.Status ?? "未知"}{Environment.NewLine}"
                + $"已登录账号  : {accountText}{Environment.NewLine}"
                + $"QQ 进程     : {processes.Count} 个，其中已加载 {injected} 个、已登录 {loggedIn} 个{Environment.NewLine}"
                + $"{Environment.NewLine}"
                + "提示：若「已加载 0 个」但 QQ 正在运行，通常是 SnowLuma 的原生组件未能挂上 QQ 进程；"
                + "请查看 SnowLuma 日志中的 [Hook] 行。";
        }

        await RefreshConnectionsAsync();
        await RefreshOneBotAsync(processes);

        // 日志跟随：勾选时才拉，避免没在看日志的人每 5 秒白拉一次。
        if (LogFollowCheck.IsChecked == true)
        {
            await LoadLogsAsync(scrollToEnd: true);
        }

        HintText.Text = $"最后更新 {DateTime.Now:HH:mm:ss}";
    }

    // 接入点状态：这一栏回答「桥接到底连上没有」。
    private async Task RefreshConnectionsAsync()
    {
        var list = await _client.GetConnectionsAsync();

        if (list.Count == 0)
        {
            ConnectionsText.Text = _client.HasSession
                ? "（没有已登录的账号，或还没有配置任何接入点）"
                : "（未登录，读不到）";
            return;
        }

        var lines = new List<string>();

        foreach (var connection in list)
        {
            lines.Add($"{connection.Nickname ?? "?"}（{connection.Uin ?? "?"}）");

            if (connection.Adapters.Count == 0)
            {
                lines.Add("    （未配置 HTTP / WS 服务）");
                continue;
            }

            foreach (var adapter in connection.Adapters)
            {
                // detail 就是「监听中」「1 个客户端」这类人类可读说明，直接透出。
                var mark = string.Equals(adapter.Status, "ok", StringComparison.OrdinalIgnoreCase) ? "✓" : "✗";
                lines.Add($"    {mark} {adapter.Name}（{adapter.Kind}）：{adapter.Detail}");
            }
        }

        ConnectionsText.Text = string.Join(Environment.NewLine, lines);
    }

    // ── OneBot 配置 ──

    // 读取配置并回填控件。
    private async Task RefreshOneBotAsync(IReadOnlyList<SnowLumaProcess> processes)
    {
        // 账号列表：优先用 /api/qq-list（只有真登录成功才有），退化到进程里的 uin。
        var accounts = await _client.GetQqListAsync();

        var uins = accounts
            .Select(a => a.Uin)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            .ToList();

        if (uins.Count == 0)
        {
            uins = processes
                .Select(p => p.Uin)
                .Where(u => !string.IsNullOrWhiteSpace(u) && u != "0")
                .Select(u => u!)
                .Distinct()
                .ToList();
        }

        _loading = true;
        try
        {
            var previous = AccountCombo.SelectedItem as string;
            AccountCombo.ItemsSource = uins;
            AccountCombo.SelectedItem = previous is not null && uins.Contains(previous)
                ? previous
                : uins.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        if (uins.Count == 0)
        {
            ConfigHintText.Text = "没有已登录的 QQ 账号，OneBot 配置暂不可读。";
            return;
        }

        await LoadConfigAsync((string?)AccountCombo.SelectedItem ?? uins[0]);
    }

    // 读取指定账号的配置并回填。
    private async Task LoadConfigAsync(string uin)
    {
        if (string.IsNullOrWhiteSpace(uin))
        {
            return;
        }

        var config = await _client.GetOneBotConfigAsync(uin);
        if (config is null)
        {
            ConfigHintText.Text = $"未能读取账号 {uin} 的 OneBot 配置。";
            return;
        }

        _config = config;
        _configUin = uin;

        var http = config.Networks?.HttpServers.FirstOrDefault();
        var ws = config.Networks?.WsServers.FirstOrDefault();

        _loading = true;
        try
        {
            HttpHostBox.Text = http?.Host ?? string.Empty;
            HttpPortBox.Text = http?.Port.ToString() ?? string.Empty;
            HttpFormatCombo.SelectedItem = http?.MessageFormat ?? "array";
            HttpReportSelfCheck.IsChecked = http?.ReportSelfMessage ?? false;

            WsHostBox.Text = ws?.Host ?? string.Empty;
            WsPortBox.Text = ws?.Port.ToString() ?? string.Empty;
            WsRoleCombo.SelectedItem = ws?.Role ?? "Universal";
        }
        finally
        {
            _loading = false;
        }

        ConfigHintText.Text = http is null && ws is null
            ? $"账号 {uin} 还没有任何接入点，可点「一键补全所需端口」。"
            : $"已读取账号 {uin} 的配置。";
    }

    private async void OnAccountChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (AccountCombo.SelectedItem is string uin)
        {
            await LoadConfigAsync(uin);
        }
    }

    private async void OnReloadConfigClick(object sender, RoutedEventArgs e)
    {
        if (AccountCombo.SelectedItem is string uin)
        {
            await LoadConfigAsync(uin);
        }
    }

    // 保存：把界面上的值写回整份配置再提交。
    private async void OnSaveConfigClick(object sender, RoutedEventArgs e)
    {
        if (_config is null || string.IsNullOrWhiteSpace(_configUin))
        {
            ConfigHintText.Text = "还没有读取到配置，无法保存。";
            return;
        }

        if (!TryReadPort(HttpPortBox.Text, "HTTP 端口", out var httpPort)
            || !TryReadPort(WsPortBox.Text, "WS 端口", out var wsPort))
        {
            return;
        }

        var networks = _config.Networks ??= new SnowLumaNetworks();

        // 就地改第一条，不重建整个列表 —— 用户可能配置了多个服务，
        // 全量替换会静默丢掉其余的。
        var http = networks.HttpServers.FirstOrDefault();
        if (http is null)
        {
            http = new SnowLumaServerEntry { Name = "http-default", Path = "/" };
            networks.HttpServers.Add(http);
        }

        http.Host = HttpHostBox.Text.Trim();
        http.Port = httpPort;
        http.MessageFormat = HttpFormatCombo.SelectedItem as string ?? "array";
        http.ReportSelfMessage = HttpReportSelfCheck.IsChecked == true;

        var ws = networks.WsServers.FirstOrDefault();
        if (ws is null)
        {
            ws = new SnowLumaServerEntry { Name = "ws-default", Path = "/" };
            networks.WsServers.Add(ws);
        }

        ws.Host = WsHostBox.Text.Trim();
        ws.Port = wsPort;
        ws.Role = WsRoleCombo.SelectedItem as string ?? "Universal";
        ws.MessageFormat = "array";

        SaveConfigButton.IsEnabled = false;
        try
        {
            var saved = await _client.SaveOneBotConfigAsync(_configUin, _config);
            ConfigHintText.Text = saved
                ? $"已保存账号 {_configUin} 的配置。改动通常需要重新加载账号才生效。"
                : "保存失败（SnowLuma 未接受，可能需要重新登录）。";
        }
        finally
        {
            SaveConfigButton.IsEnabled = true;
        }
    }

    // 一键补全桥接需要的两个端口。
    private async void OnEnsureOneBotClick(object sender, RoutedEventArgs e)
    {
        if (_config is null || string.IsNullOrWhiteSpace(_configUin))
        {
            ConfigHintText.Text = "还没有读取到配置，无法补全。";
            return;
        }

        // 复用与一键配置完全相同的逻辑，避免两处实现漂移。
        var changed = SetupDeployer.EnsureOneBotEndpoints(_config);

        if (!changed)
        {
            ConfigHintText.Text = $"HTTP {SetupDeployer.OneBotHttpPort} 与 WS {SetupDeployer.OneBotWsPort} 本来就有，无需改动。";
            return;
        }

        var saved = await _client.SaveOneBotConfigAsync(_configUin, _config);
        ConfigHintText.Text = saved
            ? $"已补上 HTTP {SetupDeployer.OneBotHttpPort} 与 WS {SetupDeployer.OneBotWsPort}。"
            : "保存失败。";

        if (saved)
        {
            await LoadConfigAsync(_configUin);
        }
    }

    // 解析端口输入；失败时把提示写到界面上。
    private bool TryReadPort(string text, string label, out int port)
    {
        if (int.TryParse(text.Trim(), out port) && port is >= 1 and <= 65535)
        {
            return true;
        }

        ConfigHintText.Text = $"{label}必须是 1–65535 之间的整数。";
        port = 0;
        return false;
    }

    // ── 其它 ──

    // 打开 SnowLuma 控制台 —— 协议同意与扫码只能在那边做。
    private void OnOpenWebUiClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _services.Endpoints.SnowLumaWebUi.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LoginStatusText.Text = $"打开浏览器失败：{ex.Message}";
        }
    }

    // 打开 SnowLuma 日志目录（排查注入问题时常用）。
    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = System.IO.Path.Combine(_services.Config.SnowLuma.InstallDirectory, "logs");
            if (System.IO.Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
                return;
            }

            LoginStatusText.Text = $"日志目录不存在：{directory}（SnowLuma 尚未启动过？）";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LoginStatusText.Text = $"打开日志目录失败：{ex.Message}";
        }
    }

    // ── 系统与存储 ──

    // 分页切换时按需加载 —— 系统与存储的数据变化很慢，
    // 没必要跟着 5 秒的状态轮询一起拉。
    private async void OnInnerTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 只在事件来自内层 TabControl 本身时处理（子控件冒泡上来的会重复触发）。
        if (!ReferenceEquals(e.OriginalSource, SnowLumaTabs) || !IsLoaded)
        {
            return;
        }

        switch ((SnowLumaTabs.SelectedItem as System.Windows.Controls.TabItem)?.Header as string)
        {
            case "系统":
                await LoadSystemAsync();
                break;

            case "安全与通知":
                await LoadSecurityAsync();
                break;
        }
    }

    private async Task LoadSystemAsync()
    {
        var info = await _client.GetSystemInfoAsync();
        var storage = await _client.GetStorageAsync();

        if (info is null && storage is null)
        {
            SystemInfoText.Text = _client.HasSession ? "（读不到系统信息）" : "（未登录）";
            StorageText.Text = "（未登录）";
            return;
        }

        if (info is not null)
        {
            SystemInfoText.Text =
                $"主机        : {info.Distro ?? info.Platform ?? "?"}（{info.Hostname ?? "?"}，{info.ArchLabel ?? "?"}）{Environment.NewLine}"
                + $"内核        : {info.Release ?? "?"}    Node {info.NodeVersion ?? "?"}{Environment.NewLine}"
                + $"CPU         : {info.Cpu?.Model ?? "?"}（{info.Cpu?.Cores ?? 0} 核，负载 {info.Cpu?.Average ?? 0:0.#}%）{Environment.NewLine}"
                + $"内存        : {FormatBytes(info.Memory?.Used ?? 0)} / {FormatBytes(info.Memory?.Total ?? 0)}（{info.Memory?.UsagePercent ?? 0:0.#}%）{Environment.NewLine}"
                + $"主机运行    : {FormatDuration(info.Uptime)}{Environment.NewLine}"
                + $"SnowLuma 已运行 : {FormatDuration(info.ProcessUptime)}";
        }

        if (storage is not null)
        {
            var lines = new List<string>
            {
                $"日志        : {FormatBytes(storage.Snapshot?.Logs?.TotalBytes ?? 0)}（{storage.Snapshot?.Logs?.FileCount ?? 0} 个文件，状态 {storage.Snapshot?.Logs?.State ?? "?"}）",
            };

            // 被丢弃的行数不为 0 说明日志已经被截断过，排查"日志怎么断了"要看这个。
            var dropped = storage.Snapshot?.Logs?.DroppedLines ?? 0;
            if (dropped > 0)
            {
                lines.Add($"             其中因超限丢弃 {dropped} 行 —— 想留全就调大日志上限");
            }

            foreach (var account in storage.Snapshot?.Accounts ?? [])
            {
                lines.Add(
                    $"账号 {account.Uin}（{account.Nickname ?? "?"}，{(account.Online ? "在线" : "离线")}）"
                    + $"：消息 {FormatBytes(account.MessagesBytes)} + 媒体 {FormatBytes(account.MediaBytes)} = {FormatBytes(account.TotalBytes)}");
            }

            lines.Add($"受管总量    : {FormatBytes(storage.Snapshot?.Totals?.ManagedBytes ?? 0)}");
            lines.Add($"上次清理    : {(string.IsNullOrWhiteSpace(storage.LastCleanup) ? "从未" : storage.LastCleanup)}");

            StorageText.Text = string.Join(Environment.NewLine, lines);

            // 回填设置；只读字段（effective）用来提示环境变量是否覆盖了用户值。
            var saved = storage.Settings?.Saved;
            if (saved is not null && !LogMaxMbBox.IsFocused && !LogRetainDaysBox.IsFocused)
            {
                LogMaxMbBox.Text = saved.LogMaxTotalMb.ToString();
                LogRetainDaysBox.Text = saved.LogRetainDays.ToString();
                LogPerUinCheck.IsChecked = saved.LogPerUin;
            }

            var overrides = storage.Settings?.Effective;
            StorageHintText.Text = overrides is not null
                && (overrides.LogMaxTotalMb != saved?.LogMaxTotalMb || overrides.LogRetainDays != saved?.LogRetainDays)
                ? "注意：实际生效值与环境变量不一致，说明有环境变量覆盖了这里的设置。"
                : string.Empty;
        }
    }

    private async void OnSaveStorageClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(LogMaxMbBox.Text.Trim(), out var maxMb) || maxMb < 1)
        {
            StorageHintText.Text = "日志上限必须是正整数（MB）。";
            return;
        }

        if (!int.TryParse(LogRetainDaysBox.Text.Trim(), out var days) || days < 1)
        {
            StorageHintText.Text = "保留天数必须是正整数。";
            return;
        }

        var settings = new SnowLumaStorageSettings
        {
            LogMaxTotalMb = maxMb,
            LogRetainDays = days,
            LogPerUin = LogPerUinCheck.IsChecked == true,
        };

        var ok = await _client.SaveStorageSettingsAsync(settings);
        StorageHintText.Text = ok ? "存储设置已保存。" : "保存失败。";

        if (ok)
        {
            await LoadSystemAsync();
        }
    }

    private async void OnCleanupClick(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            $"按当前保留策略清理过期日志与临时文件？{Environment.NewLine}{Environment.NewLine}"
            + "只删过期的，不会动账号数据与会话记录。",
            "确认清理",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            StorageHintText.Text = "已取消。";
            return;
        }

        var ok = await _client.CleanupStorageAsync();
        StorageHintText.Text = ok ? "已清理。" : "清理失败。";

        if (ok)
        {
            await LoadSystemAsync();
        }
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateText.Text = "检查中…";
        var info = await _client.CheckUpdateAsync();

        if (info is null)
        {
            UpdateText.Text = "检查失败（网络不通或未登录）。";
            return;
        }

        UpdateText.Text = info.HasUpdate
            ? $"当前 {info.Current} → 有新版本 {info.Latest}"
            : $"当前 {info.Current}，已是最新";

        _releaseUrl = info.HtmlUrl;
        OpenReleaseButton.IsEnabled = !string.IsNullOrWhiteSpace(_releaseUrl);
    }

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_releaseUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = _releaseUrl, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdateText.Text = $"打开链接失败：{ex.Message}";
        }
    }

    // 字节数按人类可读单位显示。
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    // 秒数转成"x 天 y 小时 z 分"。
    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));

        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
            : $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒";
    }

    // ── 安全与通知 ──

    // 当前是否处于"开始启用 TOTP"之后、等确认的状态。
    private bool _totpEnrolling;

    private async Task LoadSecurityAsync()
    {
        var auth = await _client.GetAuthStateAsync();
        var totp = await _client.GetTotpStateAsync();

        // 当前密码预填保存的那份 —— 改密码时多半就是它，省一次输入。
        if (string.IsNullOrEmpty(OldPasswordBox.Password))
        {
            var saved = SecureStore.Unprotect(_services.Config.SnowLuma.WebUiPasswordProtected);
            if (!string.IsNullOrEmpty(saved))
            {
                OldPasswordBox.Password = saved;
                TotpPasswordBox.Password = saved;
            }
        }

        TotpStateText.Text = totp is null
            ? (_client.HasSession ? "读不到两步验证状态。" : "未登录，读不到。")
            : totp.Enabled ? "当前：已启用。" : "当前：未启用。";

        TotpDisableButton.IsEnabled = totp?.Enabled == true;

        if (auth?.MustChangePassword == true)
        {
            SecurityHintText.Text = "服务端要求先修改密码 —— 在它看来当前密码仍是初始密码。";
        }

        await LoadNotificationsAsync();
    }

    private async Task LoadNotificationsAsync()
    {
        var config = await _client.GetNotificationsConfigAsync();
        if (config is null)
        {
            ChannelsText.Text = "（读不到通知配置）";
            return;
        }

        if (!DebounceBox.IsFocused)
        {
            DebounceBox.Text = config.DebounceSeconds.ToString();
        }

        ChannelsText.Text = $"渠道：{config.Channels.Count} 个";

        var recent = await _client.GetRecentNotificationsAsync();
        RecentNoticeText.Text = recent.Count == 0
            ? "最近通知：无"
            : "最近通知：" + string.Join(Environment.NewLine, recent.Take(5));
    }

    // 新密码输入时实时给出强度反馈 —— 比提交后被拒友好。
    private async void OnNewPasswordChanged(object sender, RoutedEventArgs e)
    {
        var password = NewPasswordBox.Password;

        if (string.IsNullOrEmpty(password))
        {
            StrengthText.Text = string.Empty;
            return;
        }

        var result = await _client.CheckPasswordStrengthAsync(password);
        StrengthText.Text = result is null
            ? "（强度检查不可用）"
            : (result.Valid ? "✓ 强度合格    " : "✗ 还不合格    ") + result.Display;
    }

    private async void OnChangePasswordClick(object sender, RoutedEventArgs e)
    {
        var oldPassword = OldPasswordBox.Password;
        var newPassword = NewPasswordBox.Password;

        if (string.IsNullOrEmpty(oldPassword) || string.IsNullOrEmpty(newPassword))
        {
            SecurityHintText.Text = "当前密码与新密码都要填。";
            return;
        }

        // 改密码可能把自己锁在外面，先说清后果。
        var answer = System.Windows.MessageBox.Show(
            $"确定修改 SnowLuma 的 WebUI 密码？{Environment.NewLine}{Environment.NewLine}"
            + "修改后当前会话可能失效，DSQQ 保存的旧密码也会作废 —— "
            + "需要重新登录一次，DSQQ 会用新密码覆盖保存。",
            "确认修改密码",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            SecurityHintText.Text = "已取消。";
            return;
        }

        ChangePasswordButton.IsEnabled = false;
        try
        {
            var result = await _client.ChangePasswordAsync(oldPassword, newPassword);

            if (result?.Success == true)
            {
                // 立刻用新密码重新登录并把凭据存下来 —— 否则下一次刷新就掉线了。
                var login = await _client.LoginAsync(newPassword);
                if (login.Outcome == LoginOutcome.Success)
                {
                    SaveCredentials(newPassword);
                    SecurityHintText.Text = "密码已修改，并用新密码重新登录成功（凭据已更新）。";
                }
                else
                {
                    SecurityHintText.Text = $"密码已修改，但用新密码重新登录失败：{login.Message}";
                }
            }
            else
            {
                SecurityHintText.Text = result?.Message ?? "修改失败（密码强度可能不足）。";
            }
        }
        finally
        {
            ChangePasswordButton.IsEnabled = true;
        }
    }

    private async void OnTotpBeginClick(object sender, RoutedEventArgs e)
    {
        // begin 的响应含一次性密钥/otpauth URL，字段名随版本可能变，
        // 所以原样展示给用户，而不是猜字段名去解析。
        var raw = await _client.BeginTotpAsync("SnowLuma", "SnowLuma");

        if (string.IsNullOrWhiteSpace(raw))
        {
            TotpSecretText.Text = "开始启用失败（可能未登录）。";
            TotpSecretText.Visibility = Visibility.Visible;
            return;
        }

        _totpEnrolling = true;
        TotpSecretText.Text =
            "把下面这段里的密钥/otpauth 链接加入你的验证器应用，然后填动态码点「确认启用」："
            + Environment.NewLine + raw;
        TotpSecretText.Visibility = Visibility.Visible;
    }

    private async void OnTotpConfirmClick(object sender, RoutedEventArgs e)
    {
        if (!_totpEnrolling)
        {
            TotpSecretText.Text = "请先点「开始启用」。";
            TotpSecretText.Visibility = Visibility.Visible;
            return;
        }

        var code = TotpCodeBox.Text.Trim();
        var password = TotpPasswordBox.Password;

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(password))
        {
            TotpSecretText.Text = "动态码与当前密码都要填。";
            return;
        }

        var result = await _client.ConfirmTotpAsync(code, password);
        TotpSecretText.Text = result?.Success == true
            ? "两步验证已启用。"
            : (result?.Message ?? "确认失败（动态码可能已过期，重来一次）。");

        if (result?.Success == true)
        {
            _totpEnrolling = false;
            await LoadSecurityAsync();
        }
    }

    private async void OnTotpDisableClick(object sender, RoutedEventArgs e)
    {
        var password = TotpPasswordBox.Password;
        var code = TotpCodeBox.Text.Trim();

        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(code))
        {
            TotpSecretText.Text = "关闭两步验证需要当前密码与一个有效动态码 —— 请在「动态码」「当前密码」两栏填好。";
            TotpSecretText.Visibility = Visibility.Visible;
            return;
        }

        var result = await _client.DisableTotpAsync(password, code);
        TotpSecretText.Text = result?.Success == true
            ? "两步验证已关闭。"
            : (result?.Message ?? "关闭失败。");

        if (result?.Success == true)
        {
            await LoadSecurityAsync();
        }
    }

    private async void OnSaveNotificationsClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DebounceBox.Text.Trim(), out var seconds) || seconds < 0)
        {
            NotificationHintText.Text = "去抖秒数必须是不小于 0 的整数。";
            return;
        }

        var config = await _client.GetNotificationsConfigAsync();
        if (config is null)
        {
            NotificationHintText.Text = "读不到通知配置，无法保存。";
            return;
        }

        // 只改去抖秒数，channels 原样带回 —— 它的结构没建模，重建会丢渠道。
        config.DebounceSeconds = seconds;

        var result = await _client.SaveNotificationsConfigAsync(config);
        NotificationHintText.Text = result?.Success == true
            ? "已保存。"
            : (result?.Message ?? "保存失败。");

        if (result?.Success == true)
        {
            await LoadNotificationsAsync();
        }
    }

    // ── 日志 ──

    // 读取日志与级别并渲染。
    // 参数 scrollToEnd：是否滚到底部（跟随模式要，手动刷新不必）。
    private async Task LoadLogsAsync(bool scrollToEnd)
    {
        // 级别只在第一次或用户要求时重取：它是服务端状态，每次刷新都拉没必要。
        if (LogLevelCombo.ItemsSource is null)
        {
            var levels = await _client.GetLogLevelAsync();
            if (levels is not null)
            {
                _loading = true;
                try
                {
                    LogLevelCombo.ItemsSource = levels.Levels;
                    LogLevelCombo.SelectedItem = levels.Level;
                }
                finally
                {
                    _loading = false;
                }
            }
        }

        var entries = await _client.GetLogsAsync();

        if (entries.Count == 0)
        {
            LogText.Text = _client.HasSession ? "（没有日志）" : "（未登录，读不到）";
            return;
        }

        // 直接拼成一大段文本，比逐条建控件快得多（300 条量级差别明显）。
        LogText.Text = string.Join(Environment.NewLine, entries.Select(e => e.Display));
        LogHintText.Text = $"共 {entries.Count} 条";

        if (scrollToEnd)
        {
            LogScroll.ScrollToEnd();
        }
    }

    private async void OnRefreshLogsClick(object sender, RoutedEventArgs e)
        => await LoadLogsAsync(scrollToEnd: true);

    // 切换日志级别。
    private async void OnLogLevelChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || LogLevelCombo.SelectedItem is not string level)
        {
            return;
        }

        var ok = await _client.SetLogLevelAsync(level);
        LogHintText.Text = ok
            ? level == "info" ? "级别已设为 info。" : $"级别已设为 {level}（排查完记得调回 info）。"
            : "级别设置失败。";
    }

    // ── 进程加载 / 卸载 ──

    private async void OnLoadProcessClick(object sender, RoutedEventArgs e)
    {
        var process = ProcessGrid.SelectedItem as SnowLumaProcess;
        if (process is null)
        {
            ProcessHintText.Text = "请先在表格里选中一个进程。";
            return;
        }

        if (process.Injected)
        {
            ProcessHintText.Text = $"PID {process.Pid} 已经加载过了。";
            return;
        }

        ProcessHintText.Text = $"正在加载 PID {process.Pid}…";
        var ok = await _client.LoadProcessAsync(process.Pid);

        ProcessHintText.Text = ok
            ? $"已把原生组件加载进 PID {process.Pid}。"
            : $"加载 PID {process.Pid} 失败（该进程可能已退出，或权限不足）。";

        await RefreshAsync();
    }

    private async void OnUnloadProcessClick(object sender, RoutedEventArgs e)
    {
        var process = ProcessGrid.SelectedItem as SnowLumaProcess;
        if (process is null)
        {
            ProcessHintText.Text = "请先在表格里选中一个进程。";
            return;
        }

        if (!process.Injected)
        {
            ProcessHintText.Text = $"PID {process.Pid} 本来就没加载。";
            return;
        }

        // 卸载会中断该账号的收发，先确认一次。
        var answer = System.Windows.MessageBox.Show(
            $"确定从 PID {process.Pid} 卸载原生组件？{Environment.NewLine}{Environment.NewLine}"
            + "该账号的 QQ 消息收发会立刻中断，直到重新加载。（不会影响 QQ 客户端本身。）",
            "确认卸载",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            ProcessHintText.Text = "已取消。";
            return;
        }

        ProcessHintText.Text = $"正在卸载 PID {process.Pid}…";
        var ok = await _client.UnloadProcessAsync(process.Pid);

        ProcessHintText.Text = ok
            ? $"已从 PID {process.Pid} 卸载。"
            : $"卸载 PID {process.Pid} 失败。";

        await RefreshAsync();
    }

    private async void OnRefreshProcessClick(object sender, RoutedEventArgs e)
    {
        var process = ProcessGrid.SelectedItem as SnowLumaProcess;
        if (process is null)
        {
            ProcessHintText.Text = "请先在表格里选中一个进程。";
            return;
        }

        var ok = await _client.RefreshProcessAsync(process.Pid);
        ProcessHintText.Text = ok
            ? $"已重新探测 PID {process.Pid}。"
            : $"重新探测 PID {process.Pid} 失败。";

        await RefreshAsync();
    }
}
