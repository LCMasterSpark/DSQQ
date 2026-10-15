using System.IO;
using DSQQ.Models;

namespace DSQQ.Services;

// 启动流程的阶段，用于向界面汇报进度。
public enum StartupStage
{
    // 启动前的环境校验。
    Checking,

    // 启动 SnowLuma。
    StartingSnowLuma,

    // 等待 SnowLuma 的 WebUI 端口就绪。
    WaitingSnowLuma,

    // 启动 qq-bridge。
    StartingBridge,

    // 等待桥接控制台端口就绪。
    WaitingBridge,

    // 探活确认整体可用。
    Verifying,

    // 流程结束（成功或失败）。
    Completed,
}

// 启动进度。
// 参数 Stage：当前阶段。
// 参数 Message：面向用户的说明。
// 参数 Succeeded：阶段是否成功（仅对最终阶段有意义，中途失败会立即结束流程）。
public readonly record struct StartupProgress(StartupStage Stage, string Message, bool Succeeded = true);

// 服务编排：一键启动 / 一键停止。
// 启动顺序（顺序不可调换）：
// 校验 QQ.exe 是否在跑 —— SnowLuma 是挂在 QQ 进程上的，QQ 没开就启动它，
// 只会建出 WebUI 而不建 OneBot（3000/3001），表现为"起来了但收不到消息"；
// 启动 SnowLuma 并等端口就绪；
// 启动 qq-bridge 并等控制台端口就绪；
// 探活 /api/status。
// 停止顺序与启动相反，并且有一条硬约束：
// 绝不按进程名批量终止（SnowLuma 与桥接都叫 node.exe，会互相误杀）；
// 一律从端口反查 PID，只终止那棵进程树；
// 桥接停止后清理 state/bridge.lock：它用锁文件做单实例保护，
// 残留会让下次启动被判定为"已有实例在运行"而拒绝启动。
// 合规边界：本类只负责"启动/停止进程"。协议同意、扫码登录
// 必须由用户本人在 SnowLuma WebUI 完成 —— SnowLuma EULA §5.4 明确禁止
// 通过自动化脚本部署其专有组件。
// 参数 config：应用配置。
public sealed class ServiceOrchestrator(AppConfig config)
{
    private readonly AppConfig _config = config ?? throw new ArgumentNullException(nameof(config));

    // 按顺序启动 SnowLuma 与 qq-bridge，并通过 汇报每一步。
    // 参数 progress：进度回调；可为 null。
    // 参数 cancellationToken：取消标记。
    // 返回：整体是否成功。
    // 本方法不抛异常：失败通过进度回调与返回值表达。
    public async Task<bool> StartAllAsync(
        IProgress<StartupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        void Report(StartupStage stage, string message, bool ok = true)
            => progress?.Report(new StartupProgress(stage, message, ok));

        try
        {
            // ── 1. 前置校验 ──
            Report(StartupStage.Checking, "校验运行环境…");

            if (_config.Startup.LaunchSnowLuma)
            {
                var qqRunning = ProcessLauncher.IsProcessRunning("QQ");
                if (qqRunning == false)
                {
                    Report(StartupStage.Completed,
                        "未检测到 QQ.exe。SnowLuma 需要挂在已登录的 QQ 进程上，请先登录 QQ 小号后重试。", false);
                    return false;
                }

                if (qqRunning is null)
                {
                    // 查不到不等于没有：给出提示但继续，避免因权限问题误拦用户。
                    Report(StartupStage.Checking, "无法确认 QQ.exe 状态（权限受限），继续尝试启动。");
                }
            }

            // ── 2. SnowLuma ──
            if (_config.Startup.LaunchSnowLuma)
            {
                var ready = await PortProbe.FindOpenPortAsync(
                    _config.SnowLuma.PreferredWebUiPort,
                    _config.SnowLuma.PortProbeCount,
                    cancellationToken).ConfigureAwait(false);

                if (ready is not null)
                {
                    Report(StartupStage.StartingSnowLuma, $"SnowLuma 已在运行（WebUI 端口 {ready}），跳过启动。");
                }
                else
                {
                    Report(StartupStage.StartingSnowLuma, "正在启动 SnowLuma…");

                    var environment = new Dictionary<string, string>();
                    if (_config.SnowLuma.InjectBootstrapPassword
                        && !string.IsNullOrEmpty(_config.SnowLuma.BootstrapPassword))
                    {
                        // 仅在用户显式开启时注入；默认关闭（见 AppConfig 中的合规说明）。
                        environment["SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD"] = _config.SnowLuma.BootstrapPassword;
                    }

                    // 用 SnowLuma 自带的 node.exe 直接启动，不走 launcher.bat：
                    // 官方 bat 依赖 PATH 上有 node，机器上没装全局 Node 时会静默失败。
                    var launched = ProcessLauncher.StartSnowLuma(
                        _config.SnowLuma.InstallDirectory,
                        environment);

                    if (!launched.Succeeded)
                    {
                        Report(StartupStage.Completed, $"SnowLuma 启动失败：{launched.Message}", false);
                        return false;
                    }

                    Report(StartupStage.WaitingSnowLuma, "等待 SnowLuma WebUI 就绪（最多 60 秒）…");

                    var port = await WaitForPortAsync(
                        _config.SnowLuma.PreferredWebUiPort,
                        _config.SnowLuma.PortProbeCount,
                        TimeSpan.FromSeconds(60),
                        cancellationToken).ConfigureAwait(false);

                    if (port is null)
                    {
                        Report(StartupStage.Completed,
                            "等待 SnowLuma WebUI 超时。请检查其日志（安装目录下 logs\\）确认是否已启动。", false);
                        return false;
                    }

                    Report(StartupStage.WaitingSnowLuma, $"SnowLuma WebUI 已就绪：端口 {port}。");
                }
            }

            // ── 3. qq-bridge ──
            if (_config.Startup.LaunchBridge)
            {
                if (await PortProbe.IsPortOpenAsync(_config.Bridge.ConsolePort, cancellationToken).ConfigureAwait(false))
                {
                    Report(StartupStage.StartingBridge, $"qq-bridge 已在运行（端口 {_config.Bridge.ConsolePort}），跳过启动。");
                }
                else
                {
                    var nodePath = ResolveNodeExecutable();
                    if (nodePath is null)
                    {
                        Report(StartupStage.Completed,
                            "未找到 node.exe。请在「设置」页指定桥接使用的 Node 可执行文件路径"
                            + "（或确认 DSH 运行时已安装）。", false);
                        return false;
                    }

                    Report(StartupStage.StartingBridge, $"正在启动 qq-bridge（{nodePath}）…");

                    // QQ_BRIDGE_SKIP_STATE_ACL：跳过桥接对 state 目录的 ACL 加固。
                    // 该加固会移除继承 ACE，可能让后续以其它身份运行的组件读不到状态文件；
                    // 官方启动脚本同样跳过它。
                    var environment = new Dictionary<string, string>
                    {
                        ["QQ_BRIDGE_SKIP_STATE_ACL"] = "1",
                        ["DSH_HOME"] = _config.Dsh.HomeDirectory,
                    };

                    var launched = ProcessLauncher.StartScript(
                        nodePath,
                        _config.Bridge.EntryRelativePath,
                        _config.Bridge.Directory,
                        environment);

                    if (!launched.Succeeded)
                    {
                        Report(StartupStage.Completed, $"qq-bridge 启动失败：{launched.Message}", false);
                        return false;
                    }

                    Report(StartupStage.WaitingBridge, "等待桥接控制台就绪（最多 30 秒）…");

                    var ready = await WaitForSinglePortAsync(
                        _config.Bridge.ConsolePort,
                        TimeSpan.FromSeconds(30),
                        cancellationToken).ConfigureAwait(false);

                    if (!ready)
                    {
                        Report(StartupStage.Completed,
                            $"等待桥接端口 {_config.Bridge.ConsolePort} 超时。"
                            + $"请查看 {Path.Combine(_config.Bridge.Directory, "state", "bridge.log")}。", false);
                        return false;
                    }

                    Report(StartupStage.WaitingBridge, $"桥接控制台已就绪：端口 {_config.Bridge.ConsolePort}。");
                }
            }

            // ── 4. 探活 ──
            Report(StartupStage.Verifying, "校验整体连通性…");
            var healthy = await PortProbe.IsPortOpenAsync(_config.Bridge.ConsolePort, cancellationToken).ConfigureAwait(false);

            Report(StartupStage.Completed,
                healthy ? "启动完成。" : "进程已启动，但桥接端口未响应，请查看日志。",
                healthy);

            return healthy;
        }
        catch (OperationCanceledException)
        {
            Report(StartupStage.Completed, "启动流程已取消。", false);
            return false;
        }
        catch (Exception ex)
        {
            // 编排层兜底：任何意外都转成可读结果，避免界面卡在"启动中"。
            Report(StartupStage.Completed, $"启动过程中出现意外错误：{ex.GetType().Name}: {ex.Message}", false);
            return false;
        }
    }

    // 停止桥接（先停，因为它依赖 SnowLuma）。
    // 参数 cancellationToken：取消标记。
    // 返回：面向用户的结果说明列表。
    public async Task<IReadOnlyList<string>> StopBridgeAsync(CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();

        var pid = await PortProcessResolver.FindListeningPidAsync(_config.Bridge.ConsolePort, cancellationToken)
            .ConfigureAwait(false);

        if (pid is null)
        {
            messages.Add($"桥接未在运行（端口 {_config.Bridge.ConsolePort} 无监听）。");
        }
        else
        {
            var result = ProcessLauncher.KillTree(pid.Value);
            messages.Add(result.Succeeded
                ? $"已停止桥接（PID {pid}）及其子进程。"
                : $"停止桥接失败：{result.Message}");
        }

        // 清理锁文件：残留会让下次启动被判为"已有实例在运行"。
        var lockFile = Path.Combine(_config.Bridge.Directory, "state", "bridge.lock");
        try
        {
            if (File.Exists(lockFile))
            {
                File.Delete(lockFile);
                messages.Add("已清理 bridge.lock。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            messages.Add($"bridge.lock 清理失败（可手动删除）：{ex.Message}");
        }

        return messages;
    }

    // 停止 SnowLuma。
    // 参数 cancellationToken：取消标记。
    // 返回：面向用户的结果说明列表。
    // SnowLuma 会同时占用 WebUI 端口与 OneBot 的两个端口，三者的 PID 相同；
    // 这里用"先 WebUI、再 OneBot"的顺序反查，任一查到即足以定位进程。
    public async Task<IReadOnlyList<string>> StopSnowLumaAsync(CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();

        var candidates = new List<int>();
        var webUi = await PortProbe
            .FindOpenPortAsync(_config.SnowLuma.PreferredWebUiPort, _config.SnowLuma.PortProbeCount, cancellationToken)
            .ConfigureAwait(false);
        if (webUi is not null)
        {
            candidates.Add(webUi.Value);
        }

        candidates.Add(3000);
        candidates.Add(3001);

        var pids = new HashSet<int>();
        foreach (var port in candidates)
        {
            var pid = await PortProcessResolver.FindListeningPidAsync(port, cancellationToken).ConfigureAwait(false);
            if (pid is not null)
            {
                pids.Add(pid.Value);
            }
        }

        if (pids.Count == 0)
        {
            messages.Add("SnowLuma 未在运行（未发现相关端口监听）。");
            return messages;
        }

        foreach (var pid in pids)
        {
            var result = ProcessLauncher.KillTree(pid);
            messages.Add(result.Succeeded
                ? $"已停止 SnowLuma（PID {pid}）。"
                : $"停止 SnowLuma 失败（PID {pid}）：{result.Message}");
        }

        return messages;
    }

    // 解析桥接要使用的 node.exe 路径。
    // 返回：可执行文件路径；未找到时为 null。
    // 优先级：配置指定 → DSH 运行时自带（实测为 24.x，满足桥接要求的 ≥ 22.13）→ PATH。
    // 复用 DSH 运行时可以避免在 C 盘之外再装一份 Node。
    private string? ResolveNodeExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_config.Bridge.NodeExecutablePath)
            && File.Exists(_config.Bridge.NodeExecutablePath))
        {
            return _config.Bridge.NodeExecutablePath;
        }

        var candidates = new[]
        {
            Path.Combine(
                _config.Dsh.HomeDirectory,
                "dsh-runtimes", "dsh-primary-runtime", "dependencies", "node", "bin", "node.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // 在给定时间窗内探测"连续端口段"里第一个可连接的端口。
    private static async Task<int?> WaitForPortAsync(
        int startPort,
        int count,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var port = await PortProbe.FindOpenPortAsync(startPort, count, cancellationToken).ConfigureAwait(false);
            if (port is not null)
            {
                return port;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // 在给定时间窗内等待单个端口就绪。
    private static async Task<bool> WaitForSinglePortAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await PortProbe.IsPortOpenAsync(port, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
