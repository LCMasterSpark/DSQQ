using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using DSQQ.Models;
using DSQQ.Services;

namespace DSQQ;

/// <summary>
/// 主窗口（P1 骨架：运行总览）。
/// </summary>
/// <remarks>
/// 当前阶段刻意保持"代码后台 + 少量直接赋值"的简单形态，先把**数据采集链路**跑通：
/// 配置读取 → 端点解析 → 端口探测 → HTTP 状态采集 → 界面呈现。
/// MVVM 分层、视图拆分与依赖注入会在 P2 引入（见 README 阶段规划），
/// 届时本文件的采集逻辑会搬到 <c>Services</c> + <c>ViewModels</c>。
/// </remarks>
public partial class MainWindow : Window
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(3);

    private static readonly Brush OnlineBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41));
    private static readonly Brush OfflineBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
    private static readonly Brush UnknownBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));

    private readonly HttpClient _http;
    private readonly CancellationTokenSource _shutdown = new();

    private AppConfig _config = AppConfig.CreateDefault();
    private ServiceEndpoints _endpoints;
    private Timer? _pollTimer;
    private bool _refreshing;

    /// <summary>
    /// 初始化主窗口：加载配置、解析端点、准备 HTTP 客户端。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        // 配置读取失败不应阻止界面启动：退化为默认值并让状态页显示出来。
        _config = AppConfigStore.TryLoad(out var loadError) ?? AppConfig.CreateDefault();
        _endpoints = ServiceEndpoints.FromConfig(_config);

        _http = new HttpClient { Timeout = HttpTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DSQQ/0.1 (+local panel)");

        UpdateFooter();
        if (!string.IsNullOrEmpty(loadError))
        {
            DetailsText.Text = $"⚠️ 配置文件读取失败，已使用默认值：{loadError}";
        }
    }

    /// <summary>
    /// 窗口加载完成后启动定时采集。
    /// </summary>
    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 立即采集一次，避免界面空窗；随后固定间隔轮询。
        _ = RefreshAllAsync();

        var interval = TimeSpan.FromMilliseconds(Math.Max(500, _config.Monitoring.PollIntervalMs));
        _pollTimer = new Timer(_ => _ = RefreshAllAsync(), null, interval, interval);

        SubtitleText.Text = $"每 {interval.TotalSeconds:0.#} 秒自动刷新";
    }

    /// <summary>
    /// 窗口关闭时停止采集并释放资源。
    /// </summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _pollTimer?.Dispose();
        _pollTimer = null;

        // 取消尚未完成的采集，避免关闭后仍有回调触碰已释放的界面元素。
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放则无需处理。
        }
    }

    /// <summary>
    /// 手动刷新。
    /// </summary>
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

    /// <summary>
    /// 采集三个服务的状态并刷新界面。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="_refreshing"/> 做重入保护：轮询间隔短于一次采集耗时时，
    /// 否则会出现并发采集与界面错乱。
    /// </remarks>
    private async Task RefreshAllAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            // 三项互不依赖，并行探测；单项失败不影响其它两项。
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
        }
        catch (Exception ex)
        {
            // 采集层的任何异常都只反映到界面，不向上抛（定时器回调里抛出会导致进程终止）。
            SubtitleText.Text = $"采集失败：{ex.GetType().Name}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// 把单个服务的探测结果反映到界面。
    /// </summary>
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

        stateText.Text = result.State switch
        {
            ProbeState.Online => "在线",
            ProbeState.Degraded => "异常",
            _ => "离线",
        };

        detailText.Text = result.Detail;
    }

    /// <summary>探测 SnowLuma：WebUI 端口是否可连。</summary>
    private async Task<ProbeResult> ProbeSnowLumaAsync(CancellationToken token)
    {
        var probe = await PortProbe.FindOpenPortAsync(
            _config.SnowLuma.PreferredWebUiPort,
            _config.SnowLuma.PortProbeCount,
            token).ConfigureAwait(false);

        if (probe is null)
        {
            return new ProbeResult(ProbeState.Offline, $"未监听到 {_config.SnowLuma.PreferredWebUiPort} 起的可用端口");
        }

        var suffix = probe.Value == _config.SnowLuma.PreferredWebUiPort
            ? string.Empty
            : $"（{_config.SnowLuma.PreferredWebUiPort} 被占用，实际 {probe.Value}）";

        return new ProbeResult(ProbeState.Online, $"WebUI http://127.0.0.1:{probe.Value} {suffix}");
    }

    /// <summary>探测 qq-bridge：控制台端口是否可连，并尽力取回运行状态。</summary>
    private async Task<ProbeResult> ProbeBridgeAsync(CancellationToken token)
    {
        var open = await PortProbe.IsPortOpenAsync(_config.Bridge.ConsolePort, token).ConfigureAwait(false);
        if (!open)
        {
            return new ProbeResult(ProbeState.Offline, $"控制台端口 {_config.Bridge.ConsolePort} 未监听");
        }

        var uri = new Uri(_endpoints.BridgeConsole, "/api/status");
        var json = await GetStringSafeAsync(uri, token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
        {
            // 端口通但接口取不到：进程可能正在启动，或该端口被别的程序占用。
            return new ProbeResult(ProbeState.Degraded, $":{_config.Bridge.ConsolePort} 已监听，但 /api/status 无响应");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var mode = GetString(root, "mode") ?? "?";
            var connected = GetBool(root, "snowluma", "connected");
            var account = GetString(root, "snowluma", "account") ?? GetString(root, "snowluma", "self") ?? "—";
            var good = GetBool(root, "snowluma", "good");
            var allowGroups = root.TryGetProperty("allowGroups", out var g) && g.ValueKind == JsonValueKind.Array
                ? g.GetArrayLength()
                : 0;
            var allowPrivate = root.TryGetProperty("allowPrivate", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.GetArrayLength()
                : 0;

            // 桥接把上游健康分为 connected（WS 通）与 good（真的收得到 QQ 数据），两者含义不同，
            // 因此这里用 Degraded 表达"连上了但收不到"，便于一眼定位到 SnowLuma 的注入问题。
            var state = connected && good ? ProbeState.Online
                : connected ? ProbeState.Degraded
                : ProbeState.Online;

            var detail = $"模式 {mode} · 账号 {account} · 群 {allowGroups} / 私聊 {allowPrivate}";
            return new ProbeResult(state, detail, new BridgeSnapshot(mode, connected, good, account, allowGroups, allowPrivate));
        }
        catch (JsonException)
        {
            return new ProbeResult(ProbeState.Degraded, $"/api/status 返回的不是合法 JSON（长度 {json.Length}）");
        }
    }

    /// <summary>探测 DSH：Web API 端口是否可连。</summary>
    private async Task<ProbeResult> ProbeDshAsync(CancellationToken token)
    {
        var open = await PortProbe.IsPortOpenAsync(_config.Dsh.Port, token).ConfigureAwait(false);
        if (!open)
        {
            return new ProbeResult(ProbeState.Offline, $"端口 {_config.Dsh.Port} 未监听");
        }

        // DSH 需要会话 Cookie 鉴权，未带凭据时返回 401 属正常现象 —— 能握上手即视为在线。
        var uri = new Uri(_endpoints.DshWeb, "/");
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            return new ProbeResult(ProbeState.Online, $"http://127.0.0.1:{_config.Dsh.Port}（HTTP {(int)response.StatusCode}）");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ProbeResult(ProbeState.Degraded, $"端口在监听但 HTTP 握手失败：{ex.GetType().Name}");
        }
    }

    /// <summary>把桥接状态拼成可读的运行细节。</summary>
    private void BuildDetails(ProbeResult bridge)
    {
        if (bridge.Snapshot is not { } snapshot)
        {
            DetailsText.Text = $"桥接未就绪，无法读取运行细节。{Environment.NewLine}"
                + $"配置目录：{_config.Bridge.Directory}{Environment.NewLine}"
                + $"日志文件：{Path.Combine(_config.Bridge.Directory, "state", "bridge.log")}";
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
            $"桥接目录      : {_config.Bridge.Directory}",
            $"桥接日志      : {Path.Combine(_config.Bridge.Directory, "state", "bridge.log")}",
            $"活动日志      : {Path.Combine(_config.Bridge.Directory, "state", "qq-activity.log")}",
            $"SnowLuma 目录 : {_config.SnowLuma.InstallDirectory}",
            $"SnowLuma 日志 : {Path.Combine(_config.SnowLuma.InstallDirectory, "logs")}",
        };

        DetailsText.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>更新底部路径提示，路径不存在时明确标出（排障时最常犯的配置错误）。</summary>
    private void UpdateFooter()
    {
        var bridgeOk = Directory.Exists(_config.Bridge.Directory);
        var snowLumaOk = Directory.Exists(_config.SnowLuma.InstallDirectory);

        FooterText.Text =
            $"桥接目录 {(bridgeOk ? "✓" : "✗ 不存在")} {_config.Bridge.Directory}    "
            + $"SnowLuma {(snowLumaOk ? "✓" : "✗ 不存在")} {_config.SnowLuma.InstallDirectory}    "
            + $"DSH {_config.Dsh.HomeDirectory}";
    }

    /// <summary>安全地 GET 一个 URL：失败一律返回 <c>null</c>，由调用方决定如何呈现。</summary>
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

    /// <summary>探测结果的呈现模型。</summary>
    private readonly record struct ProbeResult(
        ProbeState State,
        string Detail,
        BridgeSnapshot? Snapshot = null);

    /// <summary>桥接运行状态快照。</summary>
    private readonly record struct BridgeSnapshot(
        string Mode,
        bool SnowLumaConnected,
        bool SnowLumaGood,
        string Account,
        int AllowGroups,
        int AllowPrivate);

    /// <summary>服务状态。</summary>
    private enum ProbeState
    {
        /// <summary>未监听 / 无法连接。</summary>
        Offline,

        /// <summary>可用但存在需要注意的情况（如上游收不到数据、接口无响应）。</summary>
        Degraded,

        /// <summary>正常。</summary>
        Online,
    }
}
