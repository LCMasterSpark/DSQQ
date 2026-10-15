using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using System.Windows;
using DSQQ.Models;

namespace DSQQ;

/// <summary>
/// 应用程序入口。
/// </summary>
/// <remarks>
/// 职责：
/// <list type="number">
/// <item>集中兜住未处理异常 —— 本程序大量依赖"外部进程 + 本机端口 + 网络请求"，
/// 任何一处偶发异常都不应该让整个面板消失；</item>
/// <item>创建应用级服务宿主（<see cref="AppServices"/>）并暴露为静态属性，供各视图取用；</item>
/// <item>管理托盘图标宿主（<see cref="TrayIconHost"/>）的生命周期。</item>
/// </list>
/// 说明：这里用静态属性而非依赖注入容器，理由见 <see cref="AppServices"/> 的注释；
/// 等后续阶段接入两个 API 客户端、依赖图变深后再切换。
/// </remarks>
public partial class App : Application
{
    /// <summary>应用级服务宿主（组合根）。</summary>
    public static AppServices Services { get; private set; } = null!;

    /// <summary>托盘图标宿主；初始化失败时为 <c>null</c>（托盘是增强功能，不影响主流程）。</summary>
    public static TrayIconHost? Tray { get; private set; }

    /// <summary>配置读取失败时的提示（首次启动无配置属正常情况）。</summary>
    public static string? ConfigLoadError { get; private set; }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        var config = AppServices.LoadOrDefault(out var error);
        ConfigLoadError = error;
        Services = new AppServices(config);
    }

    /// <summary>
    /// 初始化托盘图标。由主窗口在 <c>Loaded</c> 时调用 —— 托盘回调需要引用主窗口，
    /// 放在窗口就绪之后可避免"回调里窗口还不存在"的时序问题。
    /// </summary>
    /// <param name="showWindow">显示主窗口的动作。</param>
    public static void InitializeTray(Action showWindow)
    {
        if (Tray is not null)
        {
            return;
        }

        try
        {
            Tray = new TrayIconHost(
                configAccessor: () => Services.Config,
                showWindow: showWindow,
                exitApplication: ExitApplication);

            if (!Tray.Initialize())
            {
                Tray.Dispose();
                Tray = null;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 托盘不可用时继续以普通窗口运行。
            Tray = null;
        }
    }

    /// <summary>
    /// 退出应用程序：先隐藏托盘图标，再关闭应用。
    /// </summary>
    /// <remarks>
    /// 必须先让图标不可见：否则进程退出前会在托盘里留下一个"幽灵图标"，
    /// 直到鼠标划过才消失。
    /// </remarks>
    public static void ExitApplication()
    {
        try
        {
            Tray?.Dispose();
        }
        catch (InvalidOperationException)
        {
            // 释放失败不应阻止退出。
        }
        finally
        {
            Tray = null;
            Current?.Shutdown();
        }
    }

    /// <summary>
    /// 注册三层异常兜底：界面线程、后台线程、未被观察的 Task。
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        // 1) 界面线程：默认行为会直接终止进程。
        DispatcherUnhandledException += (_, args) =>
        {
            ReportError("界面线程发生未处理异常", args.Exception);
            args.Handled = true;
        };

        // 2) 后台线程：会走 AppDomain 级崩溃，无法阻止，但至少要让人看见原因。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                ReportError("后台线程发生未处理异常", ex);
            }
        };

        // 3) 未被 await 的 Task 抛错：健壮性问题的早期信号，标记已观察避免升级为崩溃。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportError("存在未被观察的任务异常", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// 统一错误上报（后续替换为应用日志 + 托盘通知，并改用 MahApps 的对话框）。
    /// </summary>
    /// <param name="title">错误标题。</param>
    /// <param name="exception">异常对象。</param>
    private static void ReportError(string title, Exception exception)
    {
        try
        {
            MessageBox.Show(
                $"{title}{Environment.NewLine}{Environment.NewLine}{exception.GetType().Name}: {exception.Message}",
                "DSQQ",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // 错误处理自身绝不能再抛异常，否则会形成递归崩溃。
        }
    }
}
