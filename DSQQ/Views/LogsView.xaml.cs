using System.Net.Http;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using Button = System.Windows.Controls.Button;
using Process = System.Diagnostics.Process;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DSQQ.Services;
using Microsoft.Win32;

namespace DSQQ.Views;

/// <summary>
/// 日志中心：多来源跟随、级别与关键字过滤、诊断包导出。
/// </summary>
public partial class LogsView : UserControl
{
    /// <summary>界面最多保留的行数，防止长时间运行后内存无限增长。</summary>
    private const int MaxBufferedLines = 20_000;

    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0x86, 0x00));
    private static readonly Brush DebugBrush = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));

    private readonly AppServices _services;
    private readonly ObservableCollection<LogEntry> _visible = [];
    private readonly List<LogEntry> _buffer = [];
    private readonly CancellationTokenSource _shutdown = new();

    private readonly Dictionary<string, LogFileTail> _tails = [];
    private readonly Dictionary<string, CancellationTokenSource> _tailTasks = [];

    private string _currentSource = string.Empty;

    /// <summary>初始化日志页。</summary>
    /// <param name="services">应用服务宿主。</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 <c>null</c>。</exception>
    public LogsView(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        InitializeComponent();
        LogList.ItemsSource = _visible;

        SourceCombo.Items.Add(SourceOption.BridgeLog);
        SourceCombo.Items.Add(SourceOption.BridgeActivity);
        SourceCombo.Items.Add(SourceOption.SnowLumaLog);
        SourceCombo.SelectedIndex = 0;

        LevelCombo.Items.Add("全部");
        LevelCombo.Items.Add("仅警告与错误");
        LevelCombo.Items.Add("仅错误");
        LevelCombo.SelectedIndex = 0;

        Loaded += (_, _) => UpdateHint();
        Unloaded += (_, _) => StopAllTails();
    }

    /// <summary>日志来源选项。</summary>
    private sealed record SourceOption(string Name, Func<AppServices, string> ResolvePath)
    {
        /// <summary>桥接主日志（排查第一现场）。</summary>
        public static readonly SourceOption BridgeLog = new("桥接日志", s => s.BridgeLogPath);

        /// <summary>桥接活动日志（消息进出）。</summary>
        public static readonly SourceOption BridgeActivity = new("桥接活动日志", s => s.BridgeActivityLogPath);

        /// <summary>SnowLuma 当天的日志（含注入与上游健康）。</summary>
        public static readonly SourceOption SnowLumaLog = new("SnowLuma 日志", ResolveSnowLumaLog);

        /// <inheritdoc />
        public override string ToString() => Name;

        /// <summary>SnowLuma 日志按天轮转，这里取最近修改的一个。</summary>
        private static string ResolveSnowLumaLog(AppServices services)
        {
            var directory = Path.Combine(services.Config.SnowLuma.InstallDirectory, "logs");
            try
            {
                if (Directory.Exists(directory))
                {
                    var latest = new DirectoryInfo(directory)
                        .GetFiles("*.log")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault();

                    if (latest is not null)
                    {
                        return latest.FullName;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 取不到时回退到约定路径，界面会提示文件不存在。
            }

            return Path.Combine(directory, "snowluma.log");
        }
    }

    /// <summary>界面用的日志行。</summary>
    public sealed class LogEntry
    {
        /// <summary>来源名称。</summary>
        public required string Source { get; init; }

        /// <summary>时间文本。</summary>
        public required string TimeText { get; init; }

        /// <summary>级别文本。</summary>
        public required string LevelText { get; init; }

        /// <summary>行内容。</summary>
        public required string Text { get; init; }

        /// <summary>级别对应的颜色。</summary>
        public required Brush LevelBrush { get; init; }

        /// <summary>原始级别（用于过滤）。</summary>
        public required LogLevel Level { get; init; }
    }

    /// <summary>切换来源或级别时重建监听。</summary>
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || SourceCombo.SelectedItem is not SourceOption option)
        {
            return;
        }

        SwitchSource(option);
    }

    /// <summary>关键字变化只重新过滤，不重建监听。</summary>
    private void OnFilterTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    /// <summary>切换到指定来源。</summary>
    private void SwitchSource(SourceOption option)
    {
        if (_currentSource == option.Name)
        {
            return;
        }

        _currentSource = option.Name;
        _buffer.Clear();
        _visible.Clear();

        var path = option.ResolvePath(_services);
        UpdateHint(path);

        if (!_tails.TryGetValue(option.Name, out var tail))
        {
            tail = new LogFileTail(
                option.Name,
                path,
                _services.Config.Monitoring.TailLines,
                _services.Config.Monitoring.MaxReadBytesPerTick,
                _services.Config.Monitoring.FileWatchDebounceMs);

            _tails[option.Name] = tail;
            StartPump(option.Name, tail);
        }

        StatusText.Text = tail.Exists ? "跟随中" : $"文件不存在：{path}";
    }

    /// <summary>启动读取任务，把新行加入缓冲并刷新界面。</summary>
    private void StartPump(string source, LogFileTail tail)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _tailTasks[source] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in tail.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    var entry = new LogEntry
                    {
                        Source = line.Source,
                        TimeText = line.Timestamp?.ToString("HH:mm:ss") ?? string.Empty,
                        LevelText = LevelText(line.Level),
                        Text = line.Text,
                        LevelBrush = BrushFor(line.Level),
                        Level = line.Level,
                    };

                    // 后台线程只入缓冲，界面更新统一切回 UI 线程批量执行。
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _buffer.Add(entry);
                        if (_buffer.Count > MaxBufferedLines)
                        {
                            _buffer.RemoveRange(0, _buffer.Count - MaxBufferedLines);
                        }

                        if (Matches(entry))
                        {
                            _visible.Add(entry);
                            if (AutoScrollCheck.IsChecked == true && _visible.Count > 0)
                            {
                                LogList.ScrollIntoView(_visible[^1]);
                            }
                        }

                        // 命中告警关键字时提示一次（相同内容做去重，避免刷屏）。
                        CheckAlert(entry);
                    }, DispatcherPriority.Background).Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = $"读取失败：{ex.Message}")
                    .Task.ConfigureAwait(false);
            }
        });
    }

    /// <summary>命中告警关键字时通过托盘提示。</summary>
    private void CheckAlert(LogEntry entry)
    {
        foreach (var keyword in _services.Config.Monitoring.AlertKeywords)
        {
            if (string.IsNullOrWhiteSpace(keyword)
                || !entry.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            App.Tray?.ShowBalloon($"日志告警：{keyword}", entry.Text.Length > 180 ? entry.Text[..180] : entry.Text, isWarning: true);
            break;
        }
    }

    /// <summary>判断一行是否通过当前过滤条件。</summary>
    private bool Matches(LogEntry entry)
    {
        if (LevelCombo.SelectedIndex == 1 && entry.Level is not (LogLevel.Warn or LogLevel.Error))
        {
            return false;
        }

        if (LevelCombo.SelectedIndex == 2 && entry.Level != LogLevel.Error)
        {
            return false;
        }

        var filter = FilterBox.Text.Trim();
        if (filter.Length == 0)
        {
            return true;
        }

        // 以 / 开头按正则解析，便于复杂匹配；正则非法时退化为普通文本。
        if (filter.StartsWith('/'))
        {
            try
            {
                return Regex.IsMatch(entry.Text, filter[1..], RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                return entry.Text.Contains(filter[1..], StringComparison.OrdinalIgnoreCase);
            }
        }

        return entry.Text.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>用当前过滤条件重建可见列表。</summary>
    private void ApplyFilter()
    {
        if (!IsLoaded)
        {
            return;
        }

        _visible.Clear();
        foreach (var entry in _buffer)
        {
            if (Matches(entry))
            {
                _visible.Add(entry);
            }
        }

        StatusText.Text = $"显示 {_visible.Count} / 缓冲 {_buffer.Count} 行";
    }

    /// <summary>重新加载：丢弃缓冲与监听，重新读取文件尾部。</summary>
    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        StopAllTails();
        _tails.Clear();
        _tailTasks.Clear();
        _buffer.Clear();
        _visible.Clear();

        if (SourceCombo.SelectedItem is SourceOption option)
        {
            var name = _currentSource;
            _currentSource = string.Empty;
            SwitchSource(option);
            StatusText.Text = $"已重新加载（{name}）";
        }
    }

    /// <summary>只清空界面缓冲，不动磁盘文件。</summary>
    private void OnClearViewClick(object sender, RoutedEventArgs e)
    {
        _buffer.Clear();
        _visible.Clear();
        StatusText.Text = "视图已清空（磁盘上的日志未改动）";
    }

    /// <summary>导出诊断包：把排查常用的信息汇总成一个文本文件。</summary>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出诊断包",
                FileName = $"dsqq-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var report = await BuildDiagnosticReportAsync().ConfigureAwait(true);
            await File.WriteAllTextAsync(dialog.FileName, report, Encoding.UTF8).ConfigureAwait(true);

            StatusText.Text = $"诊断包已导出：{dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>汇总诊断信息：环境、桥接状态、端口占用、日志尾部。</summary>
    private async Task<string> BuildDiagnosticReportAsync()
    {
        var config = _services.Config;
        var builder = new StringBuilder();

        builder.AppendLine("DSQQ 诊断包");
        builder.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(new string('-', 60));

        builder.AppendLine("[路径]");
        builder.AppendLine($"桥接目录        : {config.Bridge.Directory}");
        builder.AppendLine($"SnowLuma 目录   : {config.SnowLuma.InstallDirectory}");
        builder.AppendLine($"DSH 目录        : {config.Dsh.HomeDirectory}");
        builder.AppendLine($"配置文件        : {AppServices.ConfigFilePath}");
        builder.AppendLine();

        builder.AppendLine("[端口]");
        foreach (var port in new[]
                 {
                     config.SnowLuma.PreferredWebUiPort, 3000, 3001,
                     config.Bridge.ConsolePort, config.Dsh.Port,
                 })
        {
            var pid = await PortProcessResolver.FindListeningPidAsync(port).ConfigureAwait(true);
            builder.AppendLine($"  {port,-6} {(pid is null ? "未监听" : $"PID {pid}")}");
        }

        builder.AppendLine();
        builder.AppendLine("[桥接状态]");
        try
        {
            using var client = new BridgeApiClient(_services.Endpoints.BridgeConsole);
            var status = await client.GetStatusAsync().ConfigureAwait(true);
            builder.AppendLine(status is null
                ? "  未能读取 /api/status"
                : $"  模式 {status.Mode} / DSH 就绪 {status.DshReady} / 群 {status.AllowGroups.Count} / 私聊 {status.AllowPrivate.Count}"
                  + Environment.NewLine
                  + $"  SnowLuma 连接 {status.SnowLuma?.Connected} / 接收健康 {status.SnowLuma?.Good}");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            builder.AppendLine($"  读取状态失败：{ex.Message}");
        }

        builder.AppendLine();
        builder.AppendLine("[桥接日志尾部 200 行]");
        builder.AppendLine(TailLines(_services.BridgeLogPath, 200));

        builder.AppendLine();
        builder.AppendLine("[桥接活动日志尾部 100 行]");
        builder.AppendLine(TailLines(_services.BridgeActivityLogPath, 100));

        return builder.ToString();
    }

    /// <summary>读取文件尾部若干行；失败返回说明文本。</summary>
    private static string TailLines(string path, int count)
    {
        try
        {
            if (!File.Exists(path))
            {
                return $"（文件不存在：{path}）";
            }

            var lines = File.ReadLines(path).TakeLast(count);
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"（读取失败：{ex.Message}）";
        }
    }

    /// <summary>更新底部提示。</summary>
    private void UpdateHint(string? path = null)
    {
        var config = _services.Config;
        HintText.Text = path is null
            ? $"桥接日志：{_services.BridgeLogPath}"
            : $"当前文件：{path}    告警关键字：{string.Join('、', config.Monitoring.AlertKeywords)}";
    }

    /// <summary>停止所有监听任务。</summary>
    private void StopAllTails()
    {
        foreach (var cts in _tailTasks.Values)
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // 已释放。
            }
        }

        _tailTasks.Clear();

        foreach (var tail in _tails.Values)
        {
            tail.Dispose();
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN",
        LogLevel.Debug => "DEBUG",
        _ => "INFO",
    };

    private static Brush BrushFor(LogLevel level) => level switch
    {
        LogLevel.Error => ErrorBrush,
        LogLevel.Warn => WarnBrush,
        LogLevel.Debug => DebugBrush,
        _ => InfoBrush,
    };
}
