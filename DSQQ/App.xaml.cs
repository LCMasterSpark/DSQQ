using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using System.Windows;
using DSQQ.Models;

namespace DSQQ;

// 应用程序入口。
// 职责：
// 集中兜住未处理异常 —— 本程序大量依赖"外部进程 + 本机端口 + 网络请求"，
// 任何一处偶发异常都不应该让整个面板消失；
// 创建应用级服务宿主（AppServices）并暴露为静态属性，供各视图取用；
// 管理托盘图标宿主（TrayIconHost）的生命周期。
// 说明：这里用静态属性而非依赖注入容器，理由见 AppServices 的注释；
// 等后续阶段接入两个 API 客户端、依赖图变深后再切换。
public partial class App : Application
{
    // 应用级服务宿主（组合根）。
    public static AppServices Services { get; private set; } = null!;

    // 托盘图标宿主；初始化失败时为 null（托盘是增强功能，不影响主流程）。
    public static TrayIconHost? Tray { get; private set; }

    // 配置读取失败时的提示（首次启动无配置属正常情况）。
    public static string? ConfigLoadError { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        var config = AppServices.LoadOrDefault(out var error);
        ConfigLoadError = error;
        Services = new AppServices(config);
    }

    // 初始化托盘图标。由主窗口在 Loaded 时调用 —— 托盘回调需要引用主窗口，
    // 放在窗口就绪之后可避免"回调里窗口还不存在"的时序问题。
    // 参数 showWindow：显示主窗口的动作。
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

    // 退出应用程序：先隐藏托盘图标，再关闭应用。
    // 必须先让图标不可见：否则进程退出前会在托盘里留下一个"幽灵图标"，
    // 直到鼠标划过才消失。
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

    // 注册三层异常兜底：界面线程、后台线程、未被观察的 Task。
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

        // 3) 未被 await 的 Task 抛错。
        // 注意：args.Exception 是 AggregateException，它的 Message 只是转述
        // （例如"调用线程无法访问此对象"），真正的原因在 InnerException 里。
        // 所以这里交给 TaskObserver 展开后再提示，否则排查时看不到任何调用栈。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            TaskObserver.ReportUnobserved(args.Exception);
            args.SetObserved();
        };

        // 由 TaskObserver 上报的"发射后不管"失败：提示一次，但不打断程序。
        TaskObserver.UnobservedException += (_, ex) => ReportError("后台任务发生异常", ex);
    }

    // 统一错误上报（后续替换为应用日志 + 托盘通知，并改用 MahApps 的对话框）。
    // 参数 title：错误标题。
    // 参数 exception：异常对象。
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
