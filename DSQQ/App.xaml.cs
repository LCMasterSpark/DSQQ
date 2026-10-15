using System.Windows;

namespace DSQQ;

/// <summary>
/// 应用程序入口。
/// </summary>
/// <remarks>
/// 现阶段（P1）职责：
/// <list type="bullet">
/// <item>集中兜住未处理异常 —— 本程序大量依赖"外部进程 + 本机端口 + 网络请求"，
/// 任何一处偶发异常都不应该让整个面板消失；</item>
/// <item>确定启动窗口（由 App.xaml 的 StartupUri 指定）。</item>
/// </list>
/// 依赖注入容器、托盘图标与"一键启动"编排将在后续阶段接入，此文件会随之扩展。
/// </remarks>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();
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
