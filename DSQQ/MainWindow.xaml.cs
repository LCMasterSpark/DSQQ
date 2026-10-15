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
            var connected = GetBool(root, "snowluma", "connected");
            var account = GetString(root, "snowluma", "account") ?? "—";
            var good = GetBool(root, "snowluma", "good");
            var allowGroups = root.TryGetProperty("allowGroups", out var g) && g.ValueKind == JsonValueKind.Array
                ? g.GetArrayLength()
                : 0;
            var allowPrivate = root.TryGetProperty("allowPrivate", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.GetArrayLength()
                : 0;

            // connected（WS 通）与 good（真的收得到 QQ 数据）含义不同：
            // 后者为假通常意味着 SnowLuma 的原生注入退化，因此单独用"异常"表达。
            var state = connected && good
                ? ProbeState.Online
                : connected ? ProbeState.Degraded : ProbeState.Online;

            var detail = $"模式 {mode} · 账号 {account} · 群 {allowGroups} / 私聊 {allowPrivate}";
            return new ProbeResult(
                state,
                detail,
                new BridgeSnapshot(mode, connected, good, account, allowGroups, allowPrivate));
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
            $"SnowLuma 连接 : {snapshot.SnowLumaConnected}",
            $"上游接收健康  : {snapshot.SnowLumaGood}（good=false 通常表示 SnowLuma 的原生注入收不到 QQ 数据）",
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

    private static bool GetBool(JsonElement root, params string[] path)
    {
        var value = Navigate(root, path);
        return value is { ValueKind: JsonValueKind.True };
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
        bool SnowLumaConnected,
        bool SnowLumaGood,
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
