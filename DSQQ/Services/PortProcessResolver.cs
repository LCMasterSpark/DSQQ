using System.Diagnostics;
using System.Globalization;

namespace DSQQ.Services;

// 端口 → 监听进程 PID 的解析。
// 为什么必须有它：停止服务时绝不能按镜像名杀进程。
// 本项目的 SnowLuma 与 qq-bridge 都使用 node.exe（SnowLuma 自带运行时），
// 一条 taskkill /IM node.exe 会把另一侧一起杀掉；桥接还会派生多个 MCP 子进程。
// 因此唯一安全的做法是：从端口反查 PID，只杀那一个进程树。
// 这条约束抄自依赖项目的运维脚本注释（qq-bridge/dsh-stop.bat），是踩过的坑。
// 实现上调用 netstat -ano -p TCP 并解析文本，而不是用
// System.Net.NetworkInformation.IPGlobalProperties：
// 后者能列出监听端口却拿不到归属 PID，而 PID 正是本类唯一要的东西。
public static class PortProcessResolver
{
    // 解析 netstat -ano -p TCP 的输出，找出监听指定端口的 PID。
    // 参数 netstatOutput：netstat 的完整输出（可含表头与无关行）。
    // 参数 port：目标端口。
    // 返回：监听该端口的 PID；未找到时为 null。
    // 抽成纯函数是为了可测：真实 netstat 输出在不同 Windows 版本、不同区域设置下
    // 的列宽与状态字面量会有差异，用真实样本做用例比连机器跑更可靠。
    public static int? ParseListeningPid(string netstatOutput, int port)
    {
        if (string.IsNullOrWhiteSpace(netstatOutput))
        {
            return null;
        }

        foreach (var rawLine in netstatOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            // 只要监听行：TCP 连接还有 ESTABLISHED / TIME_WAIT 等状态，
            // 其中 ESTABLISHED 行同样带 PID，若不筛掉会把客户端进程误判为服务进程。
            if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)
                || !line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // 形如：TCP  127.0.0.1:3100  0.0.0.0:0  LISTENING  12345
            if (columns.Length < 5)
            {
                continue;
            }

            if (!TryExtractPort(columns[1], out var linePort) || linePort != port)
            {
                continue;
            }

            // 最后一列是 PID。
            if (int.TryParse(columns[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                && pid > 0)
            {
                return pid;
            }
        }

        return null;
    }

    // 从 netstat 的地址列中取出端口。
    // 参数 addressColumn：形如 127.0.0.1:3100、[::1]:3100 或 0.0.0.0:3100。
    // 参数 port：解析出的端口。
    // 返回：解析成功返回 true。
    // 取最后一个冒号之后的内容：IPv6 地址形如 [::1]:3100 本身含多个冒号，
    // 按第一个冒号切分会得到错误结果。
    public static bool TryExtractPort(string? addressColumn, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(addressColumn))
        {
            return false;
        }

        var separator = addressColumn.LastIndexOf(':');
        if (separator < 0 || separator == addressColumn.Length - 1)
        {
            return false;
        }

        return int.TryParse(
            addressColumn.AsSpan(separator + 1),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out port);
    }

    // 查询当前系统上监听指定端口的进程 PID。
    // 参数 port：目标端口。
    // 参数 cancellationToken：取消标记。
    // 返回：PID；无监听或查询失败时为 null。
    public static async Task<int?> FindListeningPidAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
        {
            return null;
        }

        var output = await RunNetstatAsync(cancellationToken).ConfigureAwait(false);
        return ParseListeningPid(output, port);
    }

    // 执行 netstat -ano -p TCP 并返回标准输出；失败时返回空串。
    private static async Task<string> RunNetstatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano -p TCP",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };

            if (!process.Start())
            {
                return string.Empty;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            // netstat 不可用（极简系统或被安全软件拦截）时不应让调用方崩溃：
            // 返回空串等价于"查不到 PID"，上层会拒绝执行停止操作 —— 这是安全的失败方向。
            return string.Empty;
        }
    }
}
