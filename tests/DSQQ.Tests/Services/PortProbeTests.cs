using System.Net;
using System.Net.Sockets;
using DSQQ.Services;

namespace DSQQ.Tests.Services;

/// <summary>
/// <see cref="PortProbe"/> 的行为约束。
/// </summary>
/// <remarks>
/// 用真实 <see cref="TcpListener"/> 建立"已监听"基线，而不是打桩：
/// 端口探测的价值恰恰在于它与操作系统网络栈的真实交互，打桩会把被测逻辑架空。
/// 监听一律绑定 <c>127.0.0.1</c>，与本程序的实际使用方式一致。
/// </remarks>
public sealed class PortProbeTests
{
    /// <summary>占用一个由系统分配的临时端口并返回监听器；调用方负责释放。</summary>
    private static TcpListener ListenOnFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    /// <summary>取得监听器实际绑定的端口号。</summary>
    private static int PortOf(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    [Fact]
    public async Task IsPortOpenAsync_对已监听端口_应返回True()
    {
        using var listener = ListenOnFreePort();
        var port = PortOf(listener);

        var open = await PortProbe.IsPortOpenAsync(port, CancellationToken.None);

        Assert.True(open, $"端口 {port} 正在监听，探测结果应为可用。");
    }

    [Fact]
    public async Task IsPortOpenAsync_对已释放端口_应返回False()
    {
        // 先占一个端口再释放：这样拿到的端口号在当前进程生命周期内基本不会被复用，
        // 比"随便挑一个高位端口"可靠得多。
        int port;
        using (var listener = ListenOnFreePort())
        {
            port = PortOf(listener);
        }

        var open = await PortProbe.IsPortOpenAsync(port, CancellationToken.None);

        Assert.False(open);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public async Task IsPortOpenAsync_端口号非法_应返回False而非抛异常(int port)
    {
        // 探测层必须容错：配置被手工改错时不能把轮询打断。
        var open = await PortProbe.IsPortOpenAsync(port, CancellationToken.None);

        Assert.False(open);
    }

    [Fact]
    public async Task IsPortOpenAsync_已取消的令牌_应返回False而非抛异常()
    {
        using var listener = ListenOnFreePort();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var open = await PortProbe.IsPortOpenAsync(PortOf(listener), cts.Token);

        Assert.False(open);
    }

    [Fact]
    public async Task FindOpenPortAsync_应返回区间内第一个可用端口()
    {
        // 同时占两个端口：较小的作为"区间起点"，较大的作为区间内的目标。
        using var first = ListenOnFreePort();
        using var second = ListenOnFreePort();

        var low = Math.Min(PortOf(first), PortOf(second));
        var high = Math.Max(PortOf(first), PortOf(second));

        var found = await PortProbe.FindOpenPortAsync(low, high - low + 1, CancellationToken.None);

        Assert.Equal(low, found);
    }

    [Fact]
    public async Task FindOpenPortAsync_区间内无监听_应返回Null()
    {
        int basePort;
        using (var listener = ListenOnFreePort())
        {
            basePort = PortOf(listener);
        }

        // 只探测一个刚被释放的端口，不扫描大片区间，避免探测到机器上其它常驻服务。
        var found = await PortProbe.FindOpenPortAsync(basePort, 1, CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task FindOpenPortAsync_起点被占用时应顺延到下一个可用端口()
    {
        // 复刻 SnowLuma 的真实场景：偏好端口被占用后自行顺延（实测 5099 被占用 → 改用 5100）。
        //
        // 注意：两个端口必须相邻。早期版本用两次 TcpListener(0) 取端口，
        // 端口号大小关系随机、区间里还夹着系统分配的其它端口，
        // 于是 "found > start" 这个断言毫无意义（实测直接失败）。
        var bound = TryBindAdjacentPair(out var firstPort, out var secondPort);

        // 相邻端口不一定能同时拿到（系统临时端口本就碎片化）。这属于环境限制而非产品缺陷，
        // 因此跳过并留下原因，避免把环境噪声误报成失败。
        if (!bound)
        {
            // xunit v2 没有 Assert.SkipUnless：环境不满足时直接返回，不误报为失败。
            return;
        }

        try
        {
            // 只占用 secondPort（较小者）；firstPort（较大者）保持空闲。
            secondPort.Start();

            var found = await PortProbe.FindOpenPortAsync(PortOf(secondPort), 4, CancellationToken.None);

            Assert.Equal(PortOf(firstPort), found);
        }
        finally
        {
            // 统一释放：无论断言成功与否，两个监听端口都必须关掉。
            firstPort.Stop();
            secondPort.Stop();
        }
    }

    /// <summary>
    /// 取得一对相邻端口：返回 true 时 firstPort 的端口号恰比 secondPort 大 1。
    /// </summary>
    /// <remarks>两个监听器均保持已开始监听状态，调用方用完必须自行 Stop；失败时全部已释放。</remarks>
    private static bool TryBindAdjacentPair(out TcpListener firstPort, out TcpListener secondPort)
    {
        const int Attempts = 30;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var probe = ListenOnFreePort();
            var candidate = PortOf(probe);
            if (candidate >= 65535)
            {
                probe.Stop();
                continue;
            }

            TcpListener? low = null;
            TcpListener? high = null;
            try
            {
                low = new TcpListener(IPAddress.Loopback, candidate);
                low.Start();
                high = new TcpListener(IPAddress.Loopback, candidate + 1);
                high.Start();

                probe.Stop();
                firstPort = high;
                secondPort = low;
                return true;
            }
            catch (SocketException)
            {
                // 候选端口或它的相邻端口已被别的进程占用，释放后换一个候选重试。
                try { low?.Stop(); } catch (SocketException) { /* 释放失败不影响重试 */ }
                try { high?.Stop(); } catch (SocketException) { /* 同上 */ }
                probe.Stop();
            }
        }

        firstPort = null!;
        secondPort = null!;
        return false;
    }

    [Fact]
    public async Task FindOpenPortAsync_探测数量非正_应抛参数异常()
    {
        // 编程错误（而非运行期异常）应当尽早暴露，这里刻意选择抛出而不是静默返回 null。
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PortProbe.FindOpenPortAsync(5000, 0, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(65536)]
    public async Task FindOpenPortAsync_起点端口非法_应抛参数异常(int startPort)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PortProbe.FindOpenPortAsync(startPort, 1, CancellationToken.None));
    }

    [Fact]
    public async Task FindOpenPortAsync_数量越过端口上界_应安全截断而不绕回()
    {
        // 从 65530 起探测 100 个：越界部分必须被丢弃，不能绕回低位端口。
        var found = await PortProbe.FindOpenPortAsync(65530, 100, CancellationToken.None);

        if (found is not null)
        {
            Assert.InRange(found.Value, 65530, 65535);
        }
    }
}
