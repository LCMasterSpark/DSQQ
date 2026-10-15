using UserControl = System.Windows.Controls.UserControl;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DSQQ.Services;

namespace DSQQ.Views;

/// <summary>
/// 服务管理页：一键启动、分级停止、端口与 PID 一览。
/// </summary>
/// <remarks>
/// 交互约定：
/// <list type="bullet">
/// <item>任何耗时的启动/停止操作期间都禁用按钮并显示进度条，
/// 避免用户连点造成重复拉起进程（桥接有单实例锁，会直接拒绝启动）；</item>
/// <item>所有结果都追加到"操作记录"，而不是只弹一次对话框 ——
/// 启动是分步长的流程，用户需要回看每一步的结论。</item>
/// </list>
/// </remarks>
public partial class ServiceManagementView : UserControl
{
    private readonly AppServices _services;
    private readonly ObservableCollection<PortRow> _portRows = [];

    /// <summary>
    /// 初始化服务管理页。
    /// </summary>
    /// <param name="services">应用服务宿主。</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 <c>null</c>。</exception>
    public ServiceManagementView(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        InitializeComponent();
        PortGrid.ItemsSource = _portRows;

        Loaded += (_, _) =>
        {
            UpdateHint();
            _ = RefreshPortsAsync();
        };
    }

    /// <summary>端口行。</summary>
    /// <param name="Service">服务名。</param>
    /// <param name="Port">端口号。</param>
    /// <param name="State">监听状态。</param>
    /// <param name="Pid">监听进程 PID。</param>
    /// <param name="Note">补充说明。</param>
    public sealed record PortRow(string Service, int Port, string State, string Pid, string Note);

    /// <summary>一键启动。</summary>
    private async void OnStartAllClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        ProgressText.Text = "开始启动…";

        var progress = new Progress<StartupProgress>(p =>
        {
            // IProgress 的回调在创建它的线程（UI 线程）上执行，这里可直接触碰控件。
            ProgressText.Text = $"[{DateTime.Now:HH:mm:ss}] {p.Message}";
            AppendLog($"[{p.Stage}] {p.Message}{(p.Succeeded ? string.Empty : "  ← 失败")}");
        });

        try
        {
            await _services.Orchestrator.StartAllAsync(progress);
        }
        catch (Exception ex)
        {
            AppendLog($"启动流程异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            await RefreshPortsAsync();
        }
    }

    /// <summary>只停止桥接（排查时最常用：改完 config.json 需要重启桥接）。</summary>
    private async void OnStopBridgeClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            AppendLog("— 停止桥接 —");
            foreach (var message in await _services.Orchestrator.StopBridgeAsync())
            {
                AppendLog(message);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"停止桥接异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            await RefreshPortsAsync();
        }
    }

    /// <summary>只停止 SnowLuma。</summary>
    private async void OnStopSnowLumaClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            AppendLog("— 停止 SnowLuma —");
            foreach (var message in await _services.Orchestrator.StopSnowLumaAsync())
            {
                AppendLog(message);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"停止 SnowLuma 异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            await RefreshPortsAsync();
        }
    }

    /// <summary>全部停止：先停桥接（它依赖 SnowLuma），再停 SnowLuma。</summary>
    private async void OnStopAllClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            AppendLog("— 全部停止 —");
            foreach (var message in await _services.Orchestrator.StopBridgeAsync())
            {
                AppendLog(message);
            }

            foreach (var message in await _services.Orchestrator.StopSnowLumaAsync())
            {
                AppendLog(message);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"停止流程异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            await RefreshPortsAsync();
        }
    }

    /// <summary>刷新端口状态。</summary>
    private async void OnRefreshPortsClick(object sender, RoutedEventArgs e) => await RefreshPortsAsync();

    /// <summary>
    /// 重新采集端口与 PID。
    /// </summary>
    /// <remarks>
    /// OneBot 的两个端口是**约定值**（桥接与 SnowLuma 都按 3000/3001 通信），
    /// 配置里没有它们；这里作为只读参考一并列出，方便排查"端口被顺延"的情况。
    /// </remarks>
    private async Task RefreshPortsAsync()
    {
        var config = _services.Config;
        var targets = new (string Service, int Port, string Note)[]
        {
            ("SnowLuma WebUI", config.SnowLuma.PreferredWebUiPort, "偏好端口，可能被顺延"),
            ("OneBot HTTP", 3000, "OneBot HTTP API"),
            ("OneBot WS", 3001, "OneBot WebSocket"),
            ("qq-bridge 控制台", config.Bridge.ConsolePort, "桥接 HTTP API"),
            ("DSH Web", config.Dsh.Port, "DeepSeek Harness"),
        };

        var rows = new List<PortRow>();
        foreach (var (service, port, note) in targets)
        {
            var pid = await PortProcessResolver.FindListeningPidAsync(port).ConfigureAwait(true);
            rows.Add(new PortRow(
                service,
                port,
                pid is null ? "未监听" : "监听中",
                pid?.ToString() ?? "—",
                note));
        }

        _portRows.Clear();
        foreach (var row in rows)
        {
            _portRows.Add(row);
        }
    }

    /// <summary>切换忙碌状态：禁用按钮避免重复触发。</summary>
    private void SetBusy(bool busy)
    {
        StartAllButton.IsEnabled = !busy;
        StopBridgeButton.IsEnabled = !busy;
        StopSnowLumaButton.IsEnabled = !busy;
        StopAllButton.IsEnabled = !busy;
        RefreshPortsButton.IsEnabled = !busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>追加一行操作记录，并保持滚动可见性由外部容器决定。</summary>
    private void AppendLog(string message)
    {
        var prefix = LogText.Text == "（暂无）" ? string.Empty : LogText.Text + Environment.NewLine;
        LogText.Text = prefix + message;
    }

    /// <summary>更新底部提示：显示桥接目录与日志路径，路径不存在时明确标出。</summary>
    private void UpdateHint()
    {
        var config = _services.Config;
        var bridgeOk = System.IO.Directory.Exists(config.Bridge.Directory);
        var snowLumaOk = System.IO.Directory.Exists(config.SnowLuma.InstallDirectory);

        HintText.Text =
            $"桥接目录 {(bridgeOk ? "✓" : "✗ 不存在")} {config.Bridge.Directory}    "
            + $"SnowLuma {(snowLumaOk ? "✓" : "✗ 不存在")} {config.SnowLuma.InstallDirectory}";
    }
}
