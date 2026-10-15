using System.Diagnostics;

namespace DSQQ;

// "发射后不管"的 Task 统一入口。
//
// 为什么需要它：形如 `_ = SomeAsync()` 的写法会把异常留在 Task 里没人观察，
// 直到 GC 回收时才由终结器线程重新抛出。那时原始调用栈已经丢失，
// TaskScheduler.UnobservedTaskException 只能给出一句转述（"调用线程无法访问此对象"），
// 完全无法定位是哪一处代码。本程序大量使用定时轮询与文件跟随，这类问题尤其难查。
//
// 用法：把 fire-and-forget 调用换成 TaskObserver.FireAndForget(...)。
// 异常会在**抛出点**被记录完整堆栈，并由上层统一提示。
public static class TaskObserver
{
    // 未观察的 Task 异常（已附加观察，保留下来供上层提示）。
    public static event EventHandler<Exception>? UnobservedException;

    // 记录 fire-and-forget 任务自身的异常。
    public static event EventHandler<Exception>? FireAndForgetFailed;

    // 启动一个后台任务并观察其异常。
    // 参数 action：任务体。
    // 参数 description：用于日志的说明，便于定位调用点。
    public static void FireAndForget(Func<Task> action, string description)
    {
        ArgumentNullException.ThrowIfNull(action);

        _ = RunAsync(action, description);
    }

    // 把一个 Task 标记为已观察：失败时记录堆栈并上报。
    // 参数 task：要观察的任务。
    // 参数 description：用于日志的说明。
    public static void Observe(Task task, string description)
    {
        ArgumentNullException.ThrowIfNull(task);

        _ = ObserveAsync(task, description);
    }

    // 供 App 的全局处理器调用：展开 AggregateException 取出真正的原因。
    // 参数 raw：TaskScheduler 给出的 AggregateException（其 Message 只是转述）。
    public static void ReportUnobserved(Exception raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var actual = Unwrap(raw);
        Log("UnobservedTask", actual);
        UnobservedException?.Invoke(null, actual);
    }

    private static async Task RunAsync(Func<Task> action, string description)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(description, ex);
            FireAndForgetFailed?.Invoke(null, ex);
        }
    }

    private static async Task ObserveAsync(Task task, string description)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // AggregateException 的 Message 只是转述，真正的原因在 InnerException。
            var actual = Unwrap(ex);
            Log(description, actual);
            UnobservedException?.Invoke(null, actual);
        }
    }

    // 从 AggregateException 里取出第一个真实异常（它才是需要看堆栈的那个）。
    private static Exception Unwrap(Exception exception)
        => exception is AggregateException aggregate && aggregate.InnerException is { } inner
            ? inner
            : exception;

    // 写调试输出并保留完整堆栈；后续若接入日志文件，只改这一处。
    private static void Log(string description, Exception exception)
    {
        Debug.WriteLine($"[DSQQ] {description} 出现异常：{exception.GetType().Name}: {exception.Message}");
        Debug.WriteLine(exception.ToString());
    }
}
