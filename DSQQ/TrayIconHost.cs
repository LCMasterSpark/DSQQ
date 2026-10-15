using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using DSQQ.Models;
using WinForms = System.Windows.Forms;

namespace DSQQ;

/// <summary>
/// 系统托盘图标宿主。
/// </summary>
/// <remarks>
/// <para>
/// 技术选择：用 <see cref="WinForms.NotifyIcon"/> 而非第三方 WPF 托盘库。
/// 理由是本程序只需要"图标 + 右键菜单 + 气泡提示"三件事，
/// <c>UseWindowsForms</c> 已随 SDK 提供，不引入额外依赖更利于长期维护。
/// </para>
/// <para>
/// 图标**运行时自绘**：项目里不新增 <c>.ico</c> 二进制资源，
/// 好处是仓库保持纯代码、评审时没有不可读的二进制 diff。
/// 代价是外观朴素（一个带状态色的圆点），后续可替换为正式图标。
/// </para>
/// </remarks>
public sealed class TrayIconHost : IDisposable
{
    private readonly Func<AppConfig> _configAccessor;
    private readonly Action _showWindow;
    private readonly Action _exitApplication;
    private readonly Action? _startAll;
    private readonly Action? _stopAll;

    private WinForms.NotifyIcon? _notifyIcon;
    private Icon? _icon;
    private bool _disposed;

    /// <summary>
    /// 初始化托盘宿主（不立即创建图标，需调用 <see cref="Initialize"/>）。
    /// </summary>
    /// <param name="configAccessor">读取当前配置的委托（用于菜单文案与路径展示）。</param>
    /// <param name="showWindow">"显示主窗口"动作。</param>
    /// <param name="exitApplication">"退出程序"动作。</param>
    /// <param name="startAll">可选的"一键启动"动作。</param>
    /// <param name="stopAll">可选的"全部停止"动作。</param>
    /// <exception cref="ArgumentNullException">任一必填委托为 <c>null</c>。</exception>
    public TrayIconHost(
        Func<AppConfig> configAccessor,
        Action showWindow,
        Action exitApplication,
        Action? startAll = null,
        Action? stopAll = null)
    {
        _configAccessor = configAccessor ?? throw new ArgumentNullException(nameof(configAccessor));
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
        _startAll = startAll;
        _stopAll = stopAll;
    }

    /// <summary>
    /// 创建托盘图标与右键菜单。
    /// </summary>
    /// <returns>创建成功返回 <c>true</c>；失败（如资源管理器不可用）返回 <c>false</c>。</returns>
    /// <remarks>失败不抛异常：托盘是增强功能，缺失不应导致程序无法使用。</remarks>
    public bool Initialize()
    {
        if (_notifyIcon is not null)
        {
            return true;
        }

        try
        {
            _icon = CreateDefaultIcon();

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (_, _) => _showWindow());
            menu.Items.Add(new WinForms.ToolStripSeparator());

            if (_startAll is not null)
            {
                menu.Items.Add("一键启动", null, (_, _) => _startAll());
            }

            if (_stopAll is not null)
            {
                menu.Items.Add("全部停止", null, (_, _) => _stopAll());
            }

            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("打开桥接日志", null, (_, _) => OpenFileSafe(GetBridgeLogPath()));
            menu.Items.Add("打开 SnowLuma 日志目录", null, (_, _) => OpenDirectorySafe(GetSnowLumaLogDirectory()));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => _exitApplication());

            _notifyIcon = new WinForms.NotifyIcon
            {
                Icon = _icon,
                Text = "DSQQ —— QQ 桥接面板",
                Visible = true,
                ContextMenuStrip = menu,
            };

            // 双击托盘图标回到主窗口，这是 Windows 上的通用习惯。
            _notifyIcon.DoubleClick += (_, _) => _showWindow();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            _notifyIcon?.Dispose();
            _notifyIcon = null;
            return false;
        }
    }

    /// <summary>
    /// 弹出气泡提示（用于告警关键字命中、启动失败等需要用户注意的事件）。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="isWarning">是否为警告（决定图标类型）。</param>
    public void ShowBalloon(string title, string message, bool isWarning = false)
    {
        if (_notifyIcon is null || !_notifyIcon.Visible)
        {
            return;
        }

        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = isWarning
                ? WinForms.ToolTipIcon.Warning
                : WinForms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(5000);
        }
        catch (InvalidOperationException)
        {
            // 气泡失败不影响主流程。
        }
    }

    /// <summary>更新悬停提示文本。</summary>
    /// <param name="text">提示文本（Windows 限制约 63 字符，超出会被截断）。</param>
    public void UpdateTooltip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_notifyIcon is null)
        {
            return;
        }

        try
        {
            _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
        }
        catch (ArgumentException)
        {
            // 文本非法（含不可见字符等）时忽略。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _icon?.Dispose();
        _icon = null;
    }

    private string GetBridgeLogPath()
    {
        var config = _configAccessor();
        return Path.Combine(config.Bridge.Directory, "state", "bridge.log");
    }

    private string GetSnowLumaLogDirectory()
    {
        var config = _configAccessor();
        return Path.Combine(config.SnowLuma.InstallDirectory, "logs");
    }

    /// <summary>打开文件；不存在时退化为打开其所在目录。</summary>
    private static void OpenFileSafe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                OpenDirectorySafe(directory);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // 打开失败静默处理：这不是关键路径。
        }
    }

    private static void OpenDirectorySafe(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // 同上。
        }
    }

    /// <summary>
    /// 运行时生成托盘图标：深色圆底 + 亮色中心点。
    /// </summary>
    private static Icon CreateDefaultIcon()
    {
        const int Size = 32;

        using var bitmap = new Bitmap(Size, Size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(0x1F, 0x2A, 0x37));
            graphics.FillEllipse(background, 1, 1, Size - 2, Size - 2);

            using var accent = new SolidBrush(Color.FromArgb(0x4C, 0xC2, 0xFF));
            graphics.FillEllipse(accent, 9, 9, Size - 18, Size - 18);
        }

        // 复制句柄后再销毁位图：Icon 不持有位图的所有权，直接返回会在释放位图后失效。
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    /// <summary>所需的 Win32 互操作声明。</summary>
    private static class NativeMethods
    {
        /// <summary>销毁图标句柄，避免 GDI 对象泄漏。</summary>
        /// <param name="handle">由 <c>Bitmap.GetHicon()</c> 返回的句柄。</param>
        /// <returns>成功返回非零。</returns>
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr handle);
    }
}
