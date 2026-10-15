using System.Net.Sockets;

namespace DSQQ.Services;

// 本机端口探测。
// 用途与设计取舍：
// 本程序需要判断"某个服务是否已经起来"，最可靠的信号是端口是否可连，
// 而不是进程名 —— 例如 SnowLuma 与桥接都用 node.exe，桥接还会派生多个 MCP 子进程；
// 所有探测都带超时并吞掉网络异常：探测失败属于正常业务结果（服务没起来），
// 不应向上抛异常打断轮询；
// 连接地址固定为 127.0.0.1，不解析主机名，避免 localhost 走 IPv6 带来的抖动。
public static class PortProbe
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(800);

    // 判断本机指定端口是否能建立 TCP 连接。
    // 参数 port：端口号（1~65535）。
    // 参数 cancellationToken：取消标记。
    // 参数 timeout：连接超时；默认 800 毫秒。
    // 返回：能连上返回 true；连接被拒、超时或参数非法返回 false。
    public static async Task<bool> IsPortOpenAsync(
        int port,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? DefaultConnectTimeout);

        try
        {
            await client.ConnectAsync("127.0.0.1", port, timeoutCts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 连接被拒绝（服务未启动）、超时（被防火墙丢包）都归为"不可用"。
            return false;
        }
    }

    // 从 起连续探测，返回第一个可连接的端口。
    // 参数 startPort：起始端口。
    // 参数 count：探测个数（含起始端口）；必须为正。
    // 参数 cancellationToken：取消标记。
    // 返回：第一个可用端口；全不可用时为 null。
    // 异常 ArgumentOutOfRangeException：不为正，或 不在 1~65535 内。
    // 存在的意义：SnowLuma 的 WebUI 端口被占用时会自行顺延（实测出现过 5099 被占用后改用 5100），
    // 因此不能把端口写死在配置里。
    public static async Task<int?> FindOpenPortAsync(
        int startPort,
        int count,
        CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "探测数量必须为正数。");
        }

        if (startPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(startPort), startPort, "起始端口必须介于 1 与 65535 之间。");
        }

        // 上界同时受"探测数量"与 65535 限制：越界即停止，不绕回低位端口
        // （绕回会探测到与本服务无关的端口，产生误导性结论）。
        var endPort = Math.Min(startPort + count - 1, 65535);

        for (var port = startPort; port <= endPort; port++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsPortOpenAsync(port, cancellationToken).ConfigureAwait(false))
            {
                return port;
            }
        }

        return null;
    }
}
