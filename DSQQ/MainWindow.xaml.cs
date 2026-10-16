using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Timer = System.Threading.Timer;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using DSQQ.Models;
using DSQQ.Services;
using DSQQ.Views;

namespace DSQQ;

// 主窗口：运行总览、页面装载与托盘行为。
// 分层现状：本文件保留"状态采集 + 视图装配"的代码后台形态，采集结果直接写入控件。
// 等 P3（日志中心）与 P4/P5（两个 API 客户端）引入后，会把这些逻辑迁到 ViewModels 并改用数据绑定；
// 现在不提前抽象，是为了避免在需求尚未稳定时造出错误的抽象层。
public partial class MainWindow : Window
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(3);

    private static readonly Brush OnlineBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41));
    private static readonly Brush OfflineBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
    private static readonly Brush UnknownBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));

    private readonly AppServices _services;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _shutdown = new();

    private Timer? _pollTimer;
    private bool _refreshing;

    // 无参构造函数。
    // 必须有它：App.xaml 的 StartupUri 与 VS 设计器都是通过反射创建窗口，
    // 而反射调用不接受"可选参数"—— MainWindow(AppServices? services = null)
    // 在编译期允许无参调用，但反射看到的仍是一个带参构造函数，
    // 于是 XAML 解析报 "未找到匹配的构造函数"。
    public MainWindow()
        : this(null)
    {
    }

    // 初始化主窗口。
    // 参数 services：应用服务宿主；为 null 时回退到 App.Services（设计器预览等场景下两者都可能为空，
    // 此时退化为默认配置，界面仍可打开）。
    public MainWindow(AppServices? services)
    {
        InitializeComponent();

        _services = services ?? App.Services ?? new AppServices(AppConfig.CreateDefault());

        _http = new HttpClient { Timeout = HttpTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DSQQ/0.1 (+local panel)");

        UpdateFooter();
        if (!string.IsNullOrEmpty(App.ConfigLoadError))
        {
            DetailsText.Text = $"⚠️ 配置文件读取失败，已使用默认值：{App.ConfigLoadError}";
        }
    }

    // 当前配置（经宿主读取，保证与设置页保存后的新值一致）。
    private AppConfig CurrentConfig => _services.Config;

    // 当前端点。
    private ServiceEndpoints CurrentEndpoints => _services.Endpoints;

    // 窗口加载：装配子页面、初始化托盘、启动定时采集。
    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 各页面在此注入依赖：各自持有需要的服务，主窗口不中转数据。
        ServiceTab.Content ??= new ServiceManagementView(_services);
        BridgeConsoleTab.Content ??= new BridgeConsoleView(_services);
        SnowLumaTab.Content ??= new SnowLumaView(_services);
        LogsTab.Content ??= new LogsView(_services);
        SettingsTab.Content ??= new SettingsView(_services);

        // 托盘回调需要引用窗口，因此放在窗口就绪之后。
        App.InitializeTray(ShowFromTray);

        _ = RefreshAllAsync();

        var interval = TimeSpan.FromMilliseconds(Math.Max(500, CurrentConfig.Monitoring.PollIntervalMs));
        _pollTimer = new Timer(_ => TaskObserver.FireAndForget(RefreshAllAsync, "主窗口状态轮询"), null, interval, interval);

        SubtitleText.Text = $"每 {interval.TotalSeconds:0.#} 秒自动刷新";

        // 便利版带的是「首次运行.bat」，它传 --first-run 进来。
        // 也让"配置文件还不存在"这一情形自动触发 —— 用户下载解压后直接双击 exe 时，
        // 这就是他想要的下一步。用 Dispatcher 排到首次刷新之后再跑，避免和它抢同一批端口探测。
        if (WantsFirstRunSetup())
        {
            Dispatcher.BeginInvoke(
                new Action(() => OnRunSetupClick(this, new RoutedEventArgs())),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    // 本次启动是否应当自动跑一次一键配置。
    //
    // 只在显式带 --first-run 时跑（便利包里的「首次运行.bat」会传）。
    // 曾经用「配置文件不存在」当判据，但那会误伤：配置文件只在用户点过
    // 「设置 → 保存」后才生成，于是一个从没保存过设置、但一切正常的机器
    // 每次启动都会跑一遍配置 —— 白做一遍还可能弹出误导提示。
    // 直接双击 exe 的新用户由体检卡片引导，不需要猜测意图。
    private static bool WantsFirstRunSetup()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (string.Equals(arg, "--first-run", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // 关闭窗口：按配置最小化到托盘，或在确认后真正退出。
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.Tray is not null && CurrentConfig.Startup.MinimizeToTrayOnClose)
        {
            // 这是"点关闭按钮"的常规路径；整体退出走 App.ExitApplication()，
            // 那条路径会先释放托盘，因此不会被这里拦下。
            e.Cancel = true;
            HideToTray();
            return;
        }

        _pollTimer?.Dispose();
        _pollTimer = null;

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放则无需处理。
        }
    }

    // 最小化到托盘。
    private void OnHideToTrayClick(object sender, RoutedEventArgs e) => HideToTray();

    // 隐藏窗口并给出一次性提示（避免用户以为程序被关掉了）。
    private void HideToTray()
    {
        Hide();
        App.Tray?.ShowBalloon("DSQQ 仍在运行", "已最小化到托盘：双击图标重新打开，右键菜单可退出。");
    }

    // 从托盘恢复窗口。
    private void ShowFromTray()
    {
        // 托盘事件来自 WinForms 消息循环，不一定在 UI 线程上，统一切回 Dispatcher。
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        });
    }

    // 手动刷新。
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await RefreshAllAsync();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    // 采集三个服务的状态并刷新界面。
    // 用 _refreshing 做重入保护：轮询间隔短于一次采集耗时时，
    // 否则会出现并发采集与界面错乱。
    private async Task RefreshAllAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var snowLumaTask = ProbeSnowLumaAsync(_shutdown.Token);
            var bridgeTask = ProbeBridgeAsync(_shutdown.Token);
            var dshTask = ProbeDshAsync(_shutdown.Token);

            await Task.WhenAll(snowLumaTask, bridgeTask, dshTask);

            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            ApplyResult(SnowLumaDot, SnowLumaStateText, SnowLumaDetailText, snowLumaTask.Result);
            ApplyResult(BridgeDot, BridgeStateText, BridgeDetailText, bridgeTask.Result);
            ApplyResult(DshDot, DshStateText, DshDetailText, dshTask.Result);

            // 体检跟着一起刷：它回答的是"现在缺什么、下一步做什么"，
            // 而这些东西会随用户操作实时变化（启动服务、填白名单、登录 QQ）。
            await RefreshHealthAsync(_shutdown.Token).ConfigureAwait(true);

            BuildDetails(bridgeTask.Result);
            SubtitleText.Text = $"最后更新 {DateTime.Now:HH:mm:ss}";

            // 托盘提示同步反映桥接状态，便于窗口最小化时判断服务是否在跑。
            App.Tray?.UpdateTooltip(bridgeTask.Result.State == ProbeState.Offline
                ? "DSQQ —— 桥接未运行"
                : $"DSQQ —— 桥接{StateText(bridgeTask.Result.State)}");
        }
        catch (Exception ex)
        {
            // 采集层的任何异常都只反映到界面，不向上抛（定时器回调中抛出会导致进程终止）。
            SubtitleText.Text = $"采集失败：{ex.GetType().Name}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ── 体检 ──

    // 体检列表的一行（ItemsControl 绑定用，成员必须是公开属性）。
    private sealed record HealthRow(string Icon, string Name, string Detail, string NextStep);

    // 刷新体检：算出"现在缺什么、下一步做什么"并显示出来。
    //
    // 它回答的是首次部署时最容易卡住的问题。所有探测都是低成本的本地操作
    //（端口连接 + 一次 /api/status），跟着定时刷新一起跑不会造成负担。
    private async Task RefreshHealthAsync(CancellationToken token)
    {
        var config = CurrentConfig;

        // 端口探测彼此独立，并行做。OneBot 只要 3000 在听就算起来了。
        var dshPortTask = PortProbe.IsPortOpenAsync(config.Dsh.Port, token);
        var snowLumaPortTask = PortProbe.IsPortOpenAsync(config.SnowLuma.PreferredWebUiPort, token);
        var bridgePortTask = PortProbe.IsPortOpenAsync(config.Bridge.ConsolePort, token);
        var oneBotPortTask = PortProbe.IsPortOpenAsync(3000, token);

        await Task.WhenAll(dshPortTask, snowLumaPortTask, bridgePortTask, oneBotPortTask)
            .ConfigureAwait(true);

        if (token.IsCancellationRequested)
        {
            return;
        }

        // 管理员 QQ 与白名单以**桥接**为准 —— 它们是桥接的配置，不是本地的。
        BridgeStatus? status = null;
        if (bridgePortTask.Result)
        {
            try
            {
                using var client = new BridgeApiClient(CurrentEndpoints.BridgeConsole, TimeSpan.FromSeconds(5));
                status = await client.GetStatusAsync(token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // 读不到就让体检把这一项标成"读不到"，不因此中断整轮刷新。
            }
        }

        // 目录是否有效要看特征文件，不能只看配置里有没有填。
        var environment = new FirstRunCheck.Environment(
            DshHomeExists: Directory.Exists(config.Dsh.HomeDirectory),
            DshPortOpen: dshPortTask.Result,
            SnowLumaDir: ServiceLocator.IsSnowLuma(config.SnowLuma.InstallDirectory)
                ? config.SnowLuma.InstallDirectory
                : null,
            BridgeDir: ServiceLocator.IsBridge(config.Bridge.Directory)
                ? config.Bridge.Directory
                : null,
            SnowLumaWebUiOpen: snowLumaPortTask.Result,
            OneBotPortOpen: oneBotPortTask.Result,
            BridgeConsoleOpen: bridgePortTask.Result);

        var report = FirstRunCheck.Inspect(config, environment, status);

        HealthList.ItemsSource = report.Blocking
            .Select(item => new HealthRow(
                item.State == FirstRunCheck.CheckState.Missing ? "✗" : "!",
                item.Name,
                item.Detail,
                item.NextStep ?? string.Empty))
            .ToList();

        HealthReadyText.Visibility = report.ReadyToChat ? Visibility.Visible : Visibility.Collapsed;

        var missing = report.Items.Count(i => i.State == FirstRunCheck.CheckState.Missing);
        var pending = report.Items.Count(i => i.State == FirstRunCheck.CheckState.Pending);

        HealthSummaryText.Text = report.ReadyToChat
            ? $"6 项全部通过"
            : $"{missing} 项缺失 · {pending} 项待处理";
    }

    private async void OnRefreshHealthClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshHealthAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // 关窗时取消属于正常路径。
        }
    }

    // 一键配置：把能自动做的都做掉，需要人做的逐条说清。
    private async void OnRunSetupClick(object sender, RoutedEventArgs e)
    {
        SetupRunButton.IsEnabled = false;
        SetupResultText.Visibility = Visibility.Visible;
        SetupResultText.Text = "正在配置…";

        try
        {
            var config = CurrentConfig;
            var results = new List<SetupDeployer.StepResult>();

            // 先准备 WebUI 密码 —— 必须在 SnowLuma 启动之前，它的密码是启动时读环境变量定的。
            var generated = SetupDeployer.EnsureBootstrapPassword(config);
            results.Add(generated
                ? new SetupDeployer.StepResult(
                    "SnowLuma 管理密码",
                    SetupDeployer.StepOutcome.Done,
                    "已生成，下次启动 SnowLuma 时自动应用（你不需要记住它）")
                : new SetupDeployer.StepResult(
                    "SnowLuma 管理密码",
                    SetupDeployer.StepOutcome.Skipped,
                    "已有"));

            var deployer = new SetupDeployer(config);
            results.AddRange(await deployer.RunAsync(null, _shutdown.Token).ConfigureAwait(true));

            // 登录产生的新会话要留下，下次就不必再登一次。
            if (deployer.RefreshedSessionToken is not null)
            {
                config.SnowLuma.SessionTokenProtected = deployer.RefreshedSessionToken;
            }

            // 只有真改了东西才落盘，避免无谓写文件。
            if (generated || deployer.RefreshedSessionToken is not null)
            {
                AppConfigStore.Save(config);
            }

            SetupResultText.Text = string.Join(
                Environment.NewLine,
                results.Select(r =>
                {
                    var mark = r.Outcome switch
                    {
                        SetupDeployer.StepOutcome.Done => "✓",
                        SetupDeployer.StepOutcome.Skipped => "·",
                        SetupDeployer.StepOutcome.NeedsUser => "!",
                        _ => "✗",
                    };

                    return $"{mark} {r.Name}：{r.Message}";
                }));
        }
        catch (OperationCanceledException)
        {
            SetupResultText.Text = "已取消。";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // 配置过程只做本地 HTTP 调用，异常类型有限；不向上抛，避免定时器回调里崩进程。
            SetupResultText.Text = $"配置失败：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            SetupRunButton.IsEnabled = true;

            // 配置完立刻重体检一次，让用户看到结果。
            try
            {
                await RefreshHealthAsync(_shutdown.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // 忽略。
            }
        }
    }

    // 把单个服务的探测结果反映到界面。
    private static void ApplyResult(
        System.Windows.Shapes.Ellipse dot,
        System.Windows.Controls.TextBlock stateText,
        System.Windows.Controls.TextBlock detailText,
        ProbeResult result)
    {
        dot.Fill = result.State switch
        {
            ProbeState.Online => OnlineBrush,
            ProbeState.Degraded => UnknownBrush,
            _ => OfflineBrush,
        };

        stateText.Text = StateText(result.State);
        detailText.Text = result.Detail;
    }

    // 状态枚举的中文文案（界面与托盘共用，避免两处措辞漂移）。
    private static string StateText(ProbeState state) => state switch
    {
        ProbeState.Online => "在线",
        ProbeState.Degraded => "异常",
        _ => "离线",
    };

    // 探测 SnowLuma：WebUI 端口是否可连（端口可能被顺延，故做区间探测）。
    private async Task<ProbeResult> ProbeSnowLumaAsync(CancellationToken token)
    {
        var config = CurrentConfig;

        var probe = await PortProbe.FindOpenPortAsync(
            config.SnowLuma.PreferredWebUiPort,
            config.SnowLuma.PortProbeCount,
            token).ConfigureAwait(false);

        if (probe is null)
        {
            return new ProbeResult(ProbeState.Offline, $"未监听到 {config.SnowLuma.PreferredWebUiPort} 起的可用端口");
        }

        var suffix = probe.Value == config.SnowLuma.PreferredWebUiPort
            ? string.Empty
            : $"（{config.SnowLuma.PreferredWebUiPort} 被占用，实际 {probe.Value}）";

        return new ProbeResult(ProbeState.Online, $"WebUI http://127.0.0.1:{probe.Value} {suffix}");
    }

    // 探测 qq-bridge：控制台端口是否可连，并尽力取回运行状态。
    private async Task<ProbeResult> ProbeBridgeAsync(CancellationToken token)
    {
        var config = CurrentConfig;

        var open = await PortProbe.IsPortOpenAsync(config.Bridge.ConsolePort, token).ConfigureAwait(false);
        if (!open)
        {
            return new ProbeResult(ProbeState.Offline, $"控制台端口 {config.Bridge.ConsolePort} 未监听");
        }

        var uri = new Uri(CurrentEndpoints.BridgeConsole, "/api/status");
        var json = await GetStringSafeAsync(uri, token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
        {
            return new ProbeResult(ProbeState.Degraded, $":{config.Bridge.ConsolePort} 已监听，但 /api/status 无响应");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var mode = GetString(root, "mode") ?? "?";
            var account = GetString(root, "snowluma", "account") ?? "—";
            var snowLuma = ParseSnowLuma(root);
            var allowGroups = root.TryGetProperty("allowGroups", out var g) && g.ValueKind == JsonValueKind.Array
                ? g.GetArrayLength()
                : 0;
            var allowPrivate = root.TryGetProperty("allowPrivate", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.GetArrayLength()
                : 0;

            // connected（WS 通）与 good（真的收得到 QQ 数据）含义不同：
            // 后者为假通常意味着 SnowLuma 的原生注入退化。
            //
            // **没连上必须算异常。** 这里原先写的是 `connected ? Degraded : Online` ——
            // 没连上反而判成 Online。当初大概是为了兼容"桥接还没开始报 connected 这个键"，
            // 但它恰好盖住了最严重的那种故障：握手卡死、一个包都收不到，
            // 而总览页一片绿（2026-10-16 实际发生）。
            var bandState = SnowLumaHealthText.IsHealthy(snowLuma)
                ? ProbeState.Online
                : ProbeState.Degraded;

            var detail = $"模式 {mode} · 账号 {account} · 群 {allowGroups} / 私聊 {allowPrivate}";
            return new ProbeResult(
                bandState,
                detail,
                new BridgeSnapshot(mode, snowLuma, account, allowGroups, allowPrivate));
        }
        catch (JsonException)
        {
            return new ProbeResult(ProbeState.Degraded, $"/api/status 返回的不是合法 JSON（长度 {json.Length}）");
        }
    }

    // 探测 DSH：Web API 端口是否可连。
    private async Task<ProbeResult> ProbeDshAsync(CancellationToken token)
    {
        var config = CurrentConfig;

        var open = await PortProbe.IsPortOpenAsync(config.Dsh.Port, token).ConfigureAwait(false);
        if (!open)
        {
            return new ProbeResult(ProbeState.Offline, $"端口 {config.Dsh.Port} 未监听");
        }

        // DSH 需要会话 Cookie 鉴权，未带凭据时返回 401 属正常现象 —— 能握上手即视为在线。
        var uri = new Uri(CurrentEndpoints.DshWeb, "/");
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            return new ProbeResult(ProbeState.Online, $"http://127.0.0.1:{config.Dsh.Port}（HTTP {(int)response.StatusCode}）");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ProbeResult(ProbeState.Degraded, $"端口在监听但 HTTP 握手失败：{ex.GetType().Name}");
        }
    }

    // 把 /api/status 里的 snowluma 段整段解出来，交给 SnowLumaHealthText 判定。
    //
    // 这里不能再手写 `GetBool(root, "snowluma", "connected")`：SnowLuma 的状态压根不是一个布尔值，
    // 而是「连着 / 没连上多久了 / 连着但一个包都没收到」几种情况，少取任何一个字段都会退化成空白，
    // 而空白恰好是这次故障最坑的地方 —— 读的人分不清「正常」「不知道」「没这一项」。
    // 整段反序列化还有个好处：桥接以后往这段里加字段，这边不用改。
    private static BridgeSnowLumaStatus? ParseSnowLuma(JsonElement root)
    {
        if (!root.TryGetProperty("snowluma", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return element.Deserialize<BridgeSnowLumaStatus>();
        }
        catch (JsonException)
        {
            // 形状不认识时退到"状态未知"，而不是编一个看起来正常的值出来。
            return null;
        }
    }

    // 把桥接状态拼成可读的运行细节。
    private void BuildDetails(ProbeResult bridge)
    {
        var config = CurrentConfig;

        if (bridge.Snapshot is not { } snapshot)
        {
            DetailsText.Text = $"桥接未就绪，无法读取运行细节。{Environment.NewLine}"
                + $"配置目录：{config.Bridge.Directory}{Environment.NewLine}"
                + $"日志文件：{_services.BridgeLogPath}";
            return;
        }

        var lines = new List<string>
        {
            $"桥接模式      : {snapshot.Mode}",
            $"SnowLuma      : {SnowLumaHealthText.Describe(snapshot.SnowLuma)}",
            $"机器人账号    : {snapshot.Account}",
            $"白名单        : 群 {snapshot.AllowGroups} 个 / 私聊 {snapshot.AllowPrivate} 个",
            string.Empty,
            $"桥接目录      : {config.Bridge.Directory}",
            $"桥接日志      : {_services.BridgeLogPath}",
            $"活动日志      : {_services.BridgeActivityLogPath}",
            $"SnowLuma 目录 : {config.SnowLuma.InstallDirectory}",
            $"SnowLuma 日志 : {Path.Combine(config.SnowLuma.InstallDirectory, "logs")}",
            $"配置文件      : {AppServices.ConfigFilePath}",
        };

        DetailsText.Text = string.Join(Environment.NewLine, lines);
    }

    // 更新底部路径提示，路径不存在时明确标出（排障时最常犯的配置错误）。
    private void UpdateFooter()
    {
        var config = CurrentConfig;
        var bridgeOk = Directory.Exists(config.Bridge.Directory);
        var snowLumaOk = Directory.Exists(config.SnowLuma.InstallDirectory);

        FooterText.Text =
            $"桥接目录 {(bridgeOk ? "✓" : "✗ 不存在")} {config.Bridge.Directory}    "
            + $"SnowLuma {(snowLumaOk ? "✓" : "✗ 不存在")} {config.SnowLuma.InstallDirectory}    "
            + $"DSH {config.Dsh.HomeDirectory}";
    }

    // 安全地 GET 一个 URL：失败一律返回 null，由调用方决定如何呈现。
    private async Task<string?> GetStringSafeAsync(Uri uri, CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync(uri, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, params string[] path)
    {
        var value = Navigate(root, path);
        return value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    }

    private static JsonElement? Navigate(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    // 探测结果的呈现模型。
    private readonly record struct ProbeResult(
        ProbeState State,
        string Detail,
        BridgeSnapshot? Snapshot = null);

    // 桥接运行状态快照。
    private readonly record struct BridgeSnapshot(
        string Mode,
        BridgeSnowLumaStatus? SnowLuma,
        string Account,
        int AllowGroups,
        int AllowPrivate);

    // 服务状态。
    private enum ProbeState
    {
        // 未监听 / 无法连接。
        Offline,

        // 可用但存在需要注意的情况（如上游收不到数据、接口无响应）。
        Degraded,

        // 正常。
        Online,
    }
}
