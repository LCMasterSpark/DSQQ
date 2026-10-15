using System.Diagnostics;
using System.IO;

namespace DSQQ.Services;

/// <summary>
/// 启动/停止由本程序托管的外部进程。
/// </summary>
/// <remarks>
/// 设计约定：
/// <list type="bullet">
/// <item>所有方法**不抛异常**，统一返回 <see cref="LauncherResult"/>，
/// 让界面能直接展示失败原因（路径不存在、权限不足、可执行文件缺失等）；</item>
/// <item>停止一律走 <see cref="Process.Kill(bool)"/> 杀**进程树**：
/// 桥接会派生 MCP 子进程（<c>mcp-snowluma-safe.js</c> 等），只杀父进程会留下孤儿进程占着端口；</item>
/// <item>环境变量通过 <see cref="ProcessStartInfo.Environment"/> 传递，不用命令行拼接，
/// 避免引号与特殊字符带来的注入问题。</item>
/// </list>
/// </remarks>
public static class ProcessLauncher
{
    /// <summary>
    /// 启动一个批处理文件（用于 SnowLuma 的 <c>launcher.bat</c>）。
    /// </summary>
    /// <param name="workingDirectory">工作目录，通常为安装目录。</param>
    /// <param name="batchFileName">批处理文件名，例如 <c>launcher.bat</c>。</param>
    /// <param name="environment">额外环境变量；可为 <c>null</c>。</param>
    /// <returns>启动结果。</returns>
    public static LauncherResult StartBatch(
        string workingDirectory,
        string batchFileName,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return LauncherResult.Failed($"目录不存在：{workingDirectory}");
        }

        var batchPath = Path.Combine(workingDirectory, batchFileName);
        if (!File.Exists(batchPath))
        {
            return LauncherResult.Failed($"启动脚本不存在：{batchPath}");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // /c 执行后自动退出；/d 跳过 AutoRun，避免被注册表里的批处理劫持。
                Arguments = $"/d /c \"{batchPath}\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                {
                    startInfo.Environment[key] = value;
                }
            }

            var process = Process.Start(startInfo);
            return process is null
                ? LauncherResult.Failed("Process.Start 返回 null（无法创建进程）")
                : LauncherResult.Ok($"已启动 {batchFileName}", process.Id);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return LauncherResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 用指定的可执行文件启动一个脚本（用于 qq-bridge：<c>node.exe src/bridge.js</c>）。
    /// </summary>
    /// <param name="executablePath">可执行文件绝对路径（通常为 node.exe）。</param>
    /// <param name="scriptRelativePath">脚本相对路径，例如 <c>src\bridge.js</c>。</param>
    /// <param name="workingDirectory">工作目录（桥接根目录）。</param>
    /// <param name="environment">额外环境变量；可为 <c>null</c>。</param>
    /// <returns>启动结果。</returns>
    public static LauncherResult StartScript(
        string executablePath,
        string scriptRelativePath,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return LauncherResult.Failed($"可执行文件不存在：{executablePath}");
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return LauncherResult.Failed($"目录不存在：{workingDirectory}");
        }

        var scriptPath = Path.Combine(workingDirectory, scriptRelativePath);
        if (!File.Exists(scriptPath))
        {
            return LauncherResult.Failed($"入口脚本不存在：{scriptPath}");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Node 的入口脚本用 ArgumentList 传参：带空格的路径（本机路径确实含中文与空格）
            // 不会因为手工拼引号而出错。
            startInfo.ArgumentList.Add(scriptPath);

            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                {
                    startInfo.Environment[key] = value;
                }
            }

            var process = Process.Start(startInfo);
            return process is null
                ? LauncherResult.Failed("Process.Start 返回 null（无法创建进程）")
                : LauncherResult.Ok($"已启动 {Path.GetFileName(scriptPath)}", process.Id);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return LauncherResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 终止指定进程及其子进程树。
    /// </summary>
    /// <param name="processId">目标 PID。</param>
    /// <returns>终止结果。</returns>
    /// <remarks>
    /// 调用方**必须**先用 <see cref="PortProcessResolver"/> 从端口反查出 PID，
    /// 绝不可按进程名批量终止（详见该类的说明）。
    /// </remarks>
    public static LauncherResult KillTree(int processId)
    {
        if (processId <= 0)
        {
            return LauncherResult.Failed($"非法 PID：{processId}");
        }

        try
        {
            using var process = Process.GetProcessById(processId);

            // Kill(entireProcessTree: true) 会连带结束子进程，
            // 这正是桥接停止时需要的（它的 MCP 子进程不会自己退出）。
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);

            return LauncherResult.Ok($"已终止 PID {processId} 及其子进程");
        }
        catch (ArgumentException)
        {
            // 进程已不存在 —— 对"停止"这个意图而言，结果与成功等价。
            return LauncherResult.Ok($"PID {processId} 已不存在");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return LauncherResult.Failed($"终止 PID {processId} 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 判断指定进程名是否有实例在运行（用于"先登录 QQ 小号"的前置校验）。
    /// </summary>
    /// <param name="processName">进程名，不含扩展名，例如 <c>QQ</c>。</param>
    /// <returns>存在运行中的实例返回 <c>true</c>；查询失败返回 <c>null</c>（区别于"确定没有"）。</returns>
    public static bool? IsProcessRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        try
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>进程启动/终止的结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="Message">人类可读的说明（成功与失败都会有）。</param>
/// <param name="ProcessId">启动成功时的子进程 PID；不适用时为 <c>null</c>。</param>
public readonly record struct LauncherResult(bool Succeeded, string Message, int? ProcessId = null)
{
    /// <summary>构造成功结果。</summary>
    /// <param name="message">说明文本。</param>
    /// <param name="processId">子进程 PID。</param>
    public static LauncherResult Ok(string message, int? processId = null) => new(true, message, processId);

    /// <summary>构造失败结果。</summary>
    /// <param name="message">失败原因。</param>
    public static LauncherResult Failed(string message) => new(false, message);
}
