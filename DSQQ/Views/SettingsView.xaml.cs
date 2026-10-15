using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DSQQ.Models;
using DSQQ.Services;

namespace DSQQ.Views;

// 设置页：编辑并保存 AppConfig，保存后立即生效。
// 健壮性约定：
// 读取用户输入一律走安全解析（TryGetPort / TryGetInt），
// 输入非法时给出字段级提示而不是抛异常或静默取默认值；
// 保存前做一次整体校验，任何一项不合法就整体拒绝 —— 避免存进一份半对半错的配置；
// 路径输入框在编辑时就实时反馈"目录存在 / 不存在"，因为路径写错是启动失败的头号原因。
public partial class SettingsView : UserControl
{
    private readonly AppServices _services;

    // 初始化设置页。
    // 参数 services：应用服务宿主。
    // 异常 ArgumentNullException：为 null。
    public SettingsView(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        InitializeComponent();

        Loaded += (_, _) => ReloadFromConfig();

        // 实时校验：路径类输入框失焦即反馈存在性。
        SnowLumaDirBox.LostFocus += (_, _) => ValidateDirectory(SnowLumaDirBox, SnowLumaDirState);
        BridgeDirBox.LostFocus += (_, _) => ValidateDirectory(BridgeDirBox, BridgeDirState);
    }

    // 从当前配置填充界面。
    private void ReloadFromConfig()
    {
        var config = _services.Config;

        SnowLumaDirBox.Text = config.SnowLuma.InstallDirectory;
        SnowLumaLauncherBox.Text = config.SnowLuma.LauncherFileName;
        SnowLumaPortBox.Text = config.SnowLuma.PreferredWebUiPort.ToString(CultureInfo.InvariantCulture);
        BridgeDirBox.Text = config.Bridge.Directory;
        BridgePortBox.Text = config.Bridge.ConsolePort.ToString(CultureInfo.InvariantCulture);
        BridgeEntryBox.Text = config.Bridge.EntryRelativePath;
        NodePathBox.Text = config.Bridge.NodeExecutablePath ?? string.Empty;
        DshPortBox.Text = config.Dsh.Port.ToString(CultureInfo.InvariantCulture);

        LaunchSnowLumaCheck.IsChecked = config.Startup.LaunchSnowLuma;
        LaunchBridgeCheck.IsChecked = config.Startup.LaunchBridge;
        MinimizeToTrayCheck.IsChecked = config.Startup.MinimizeToTrayOnClose;
        AutoStartCheck.IsChecked = config.Startup.AutoStartOnLaunch;
        InjectPasswordCheck.IsChecked = config.SnowLuma.InjectBootstrapPassword;
        BootstrapPasswordBox.Text = config.SnowLuma.BootstrapPassword ?? string.Empty;

        PollIntervalBox.Text = config.Monitoring.PollIntervalMs.ToString(CultureInfo.InvariantCulture);
        TailLinesBox.Text = config.Monitoring.TailLines.ToString(CultureInfo.InvariantCulture);
        AlertKeywordsBox.Text = string.Join(Environment.NewLine, config.Monitoring.AlertKeywords);

        ValidateDirectory(SnowLumaDirBox, SnowLumaDirState);
        ValidateDirectory(BridgeDirBox, BridgeDirState);

        ConfigPathText.Text = $"配置文件：{AppServices.ConfigFilePath}";
        StatusText.Text = "已载入当前配置。";
    }

    // 保存设置。
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = BuildConfigFromUi();
            _services.ApplyConfig(config);

            StatusText.Text = $"已保存并于 {DateTime.Now:HH:mm:ss} 生效。";
            ValidateDirectory(SnowLumaDirBox, SnowLumaDirState);
            ValidateDirectory(BridgeDirBox, BridgeDirState);
        }
        catch (InvalidOperationException ex)
        {
            // 校验失败：明确指出是哪个字段，便于用户立即修正。
            StatusText.Text = $"未保存：{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"写入配置文件失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    // 放弃修改并重新载入。
    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        var reloaded = AppConfigStore.TryLoad(out var error);
        if (reloaded is null)
        {
            StatusText.Text = $"重载失败（保留当前配置）：{error}";
            return;
        }

        _services.ApplyConfig(reloaded);
        ReloadFromConfig();
        StatusText.Text = "已放弃修改并重载磁盘上的配置。";
    }

    // 打开配置文件所在目录（便于手工编辑或备份）。
    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(AppServices.ConfigFilePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                StatusText.Text = "配置目录尚未创建（保存一次设置后即会生成）。";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            StatusText.Text = $"打开目录失败：{ex.Message}";
        }
    }

    // 用界面上的值构造配置对象。
    // 异常 InvalidOperationException：存在非法输入时抛出，消息中含字段名。
    private AppConfig BuildConfigFromUi()
    {
        var config = _services.Config;

        config.SnowLuma.InstallDirectory = RequireText(SnowLumaDirBox, "SnowLuma 安装目录");
        config.SnowLuma.LauncherFileName = RequireText(SnowLumaLauncherBox, "SnowLuma 启动脚本");
        config.SnowLuma.PreferredWebUiPort = RequirePort(SnowLumaPortBox, "SnowLuma WebUI 端口");

        config.Bridge.Directory = RequireText(BridgeDirBox, "qq-bridge 目录");
        config.Bridge.ConsolePort = RequirePort(BridgePortBox, "桥接控制台端口");
        config.Bridge.EntryRelativePath = RequireText(BridgeEntryBox, "桥接入口相对路径");

        var nodePath = NodePathBox.Text.Trim();
        config.Bridge.NodeExecutablePath = string.IsNullOrEmpty(nodePath) ? null : nodePath;

        config.Dsh.Port = RequirePort(DshPortBox, "DSH 端口");

        config.Startup.LaunchSnowLuma = LaunchSnowLumaCheck.IsChecked == true;
        config.Startup.LaunchBridge = LaunchBridgeCheck.IsChecked == true;
        config.Startup.MinimizeToTrayOnClose = MinimizeToTrayCheck.IsChecked == true;
        config.Startup.AutoStartOnLaunch = AutoStartCheck.IsChecked == true;

        config.SnowLuma.InjectBootstrapPassword = InjectPasswordCheck.IsChecked == true;
        var password = BootstrapPasswordBox.Text.Trim();
        config.SnowLuma.BootstrapPassword = string.IsNullOrEmpty(password) ? null : password;

        if (config.SnowLuma.InjectBootstrapPassword && string.IsNullOrEmpty(config.SnowLuma.BootstrapPassword))
        {
            throw new InvalidOperationException("已勾选注入启动密码，但密码为空。");
        }

        config.Monitoring.PollIntervalMs = RequireInt(PollIntervalBox, "状态轮询间隔", min: 500, max: 60_000);
        config.Monitoring.TailLines = RequireInt(TailLinesBox, "日志初始加载行数", min: 100, max: 200_000);

        config.Monitoring.AlertKeywords = AlertKeywordsBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return config;
    }

    // 读取必填文本字段。
    private static string RequireText(TextBox box, string fieldName)
    {
        var value = box.Text.Trim();
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"{fieldName}不能为空。");
        }

        return value;
    }

    // 读取端口字段（1~65535）。
    private static int RequirePort(TextBox box, string fieldName) => RequireInt(box, fieldName, 1, 65535);

    // 读取整数字段并做范围校验。
    private static int RequireInt(TextBox box, string fieldName, int min, int max)
    {
        if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"{fieldName}必须是整数（当前：\"{box.Text.Trim()}\"）。");
        }

        if (value < min || value > max)
        {
            throw new InvalidOperationException($"{fieldName}必须在 {min}~{max} 之间（当前：{value}）。");
        }

        return value;
    }

    // 校验目录是否存在并更新提示文本。
    private static void ValidateDirectory(TextBox box, TextBlock stateText)
    {
        var path = box.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            stateText.Text = "✗ 空";
            stateText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            return;
        }

        var exists = Directory.Exists(path);
        stateText.Text = exists ? "✓ 存在" : "✗ 不存在";
        stateText.Foreground = exists
            ? System.Windows.Media.Brushes.SeaGreen
            : System.Windows.Media.Brushes.OrangeRed;
    }
}
