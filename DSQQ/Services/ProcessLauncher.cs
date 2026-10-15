using System.Diagnostics;
using System.IO;

namespace DSQQ.Services;

// 启动/停止由本程序托管的外部进程。
// 设计约定：
// 所有方法不抛异常，统一返回 LauncherResult，
// 让界面能直接展示失败原因（路径不存在、权限不足、可执行文件缺失等）；
// 停止一律走 Process.Kill(bool) 杀进程树：
// 桥接会派生 MCP 子进程（mcp-snowluma-safe.js 等），只杀父进程会留下孤儿进程占着端口；
// 环境变量通过 ProcessStartInfo.Environment 传递，不用命令行拼接，
// 避免引号与特殊字符带来的注入问题。
public static class ProcessLauncher
{
    // 启动一个批处理文件（用于 SnowLuma 的 launcher.bat）。
    // 参数 workingDirectory：工作目录，通常为安装目录。
    // 参数 batchFileName：批处理文件名，例如 launcher.bat。
    // 参数 environment：额外环境变量；可为 null。
    // 返回：启动结果。
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

    // 用指定的可执行文件启动一个脚本（用于 qq-bridge：node.exe src/bridge.js）。
    // 参数 executablePath：可执行文件绝对路径（通常为 node.exe）。
    // 参数 scriptRelativePath：脚本相对路径，例如 src\bridge.js。
    // 参数 workingDirectory：工作目录（桥接根目录）。
    // 参数 environment：额外环境变量；可为 null。
    // 返回：启动结果。
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

    // 终止指定进程及其子进程树。
    // 参数 processId：目标 PID。
    // 返回：终止结果。
    // 调用方必须先用 PortProcessResolver 从端口反查出 PID，
    // 绝不可按进程名批量终止（详见该类的说明）。
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

    // 判断指定进程名是否有实例在运行（用于"先登录 QQ 小号"的前置校验）。
    // 参数 processName：进程名，不含扩展名，例如 QQ。
    // 返回：存在运行中的实例返回 true；查询失败返回 null（区别于"确定没有"）。
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

// 进程启动/终止的结果。
// 参数 Succeeded：是否成功。
// 参数 Message：人类可读的说明（成功与失败都会有）。
// 参数 ProcessId：启动成功时的子进程 PID；不适用时为 null。
public readonly record struct LauncherResult(bool Succeeded, string Message, int? ProcessId = null)
{
    // 构造成功结果。
    // 参数 message：说明文本。
    // 参数 processId：子进程 PID。
    public static LauncherResult Ok(string message, int? processId = null) => new(true, message, processId);

    // 构造失败结果。
    // 参数 message：失败原因。
    public static LauncherResult Failed(string message) => new(false, message);
}
