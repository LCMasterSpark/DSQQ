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
