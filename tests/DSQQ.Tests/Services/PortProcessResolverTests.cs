using System.Net;
using System.Net.Sockets;
using DSQQ.Services;

namespace DSQQ.Tests.Services;

// PortProcessResolver 的行为约束。
// 这个类是"安全停止服务"的唯一依据：它解析出 PID，上层只杀这一个进程树。
// 一旦解析错误（例如把 ESTABLISHED 行的客户端 PID 当成服务 PID），
// 就会杀错进程 —— 在真实场景里可能把用户的 QQ 或浏览器杀掉。
// 因此这里既覆盖文本解析的边界，也用真实监听端口做一次端到端校验。
public sealed class PortProcessResolverTests
{
    // 一段真实形状的 netstat 输出（含 IPv4/IPv6、监听与已建立连接）。
    private const string SampleOutput = """
          Proto  Local Address          Foreign Address        State           PID
          TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1156
          TCP    127.0.0.1:3100         0.0.0.0:0              LISTENING       44656
          TCP    127.0.0.1:3100         127.0.0.1:50288        ESTABLISHED     44656
          TCP    127.0.0.1:5099         0.0.0.0:0              LISTENING       19700
          TCP    [::1]:5099             [::]:0                 LISTENING       19700
          TCP    127.0.0.1:19387        0.0.0.0:0              LISTENING       42484
        """;

    [Fact]
    public void ParseListeningPid_应解析出监听端口的PID()
    {
        Assert.Equal(44656, PortProcessResolver.ParseListeningPid(SampleOutput, 3100));
        Assert.Equal(19700, PortProcessResolver.ParseListeningPid(SampleOutput, 5099));
        Assert.Equal(42484, PortProcessResolver.ParseListeningPid(SampleOutput, 19387));
    }

    [Fact]
    public void ParseListeningPid_不应把ESTABLISHED行的客户端当成服务()
    {
        // 这条用例钉住的是"筛掉非 LISTENING 行"这一前提：
        // 若忘记过滤，同一端口的 ESTABLISHED 行（客户端连接）也可能被解析出 PID。
        // 这里用一个只有 ESTABLISHED、没有 LISTENING 的端口来验证会被忽略。
        const string onlyEstablished = """
              TCP    127.0.0.1:61234        127.0.0.1:3100         ESTABLISHED     11111
            """;

        Assert.Null(PortProcessResolver.ParseListeningPid(onlyEstablished, 61234));
    }

    [Fact]
    public void ParseListeningPid_应正确处理IPv6地址形式()
    {
        // IPv6 地址形如 [::1]:5099，按第一个冒号切分会解析失败。
        const string ipv6Only = """
              TCP    [::]:8080             [::]:0                 LISTENING       999
            """;

        Assert.Equal(999, PortProcessResolver.ParseListeningPid(ipv6Only, 8080));
    }

    [Fact]
    public void ParseListeningPid_端口不存在时应返回Null()
    {
        Assert.Null(PortProcessResolver.ParseListeningPid(SampleOutput, 55999));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("没有任何表格内容")]
    [InlineData("TCP    127.0.0.1:3100    0.0.0.0:0    LISTENING")] // 缺 PID 列
    public void ParseListeningPid_输入异常时应返回Null而非抛异常(string output)
    {
        var pid = PortProcessResolver.ParseListeningPid(output, 3100);

        Assert.Null(pid);
    }

    [Theory]
    [InlineData("127.0.0.1:3100", 3100)]
    [InlineData("[::1]:5099", 5099)]
    [InlineData("0.0.0.0:80", 80)]
    [InlineData("[::]:8080", 8080)]
    public void TryExtractPort_应取出最后一个冒号后的端口(string address, int expected)
    {
        Assert.True(PortProcessResolver.TryExtractPort(address, out var port));
        Assert.Equal(expected, port);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:")]
    [InlineData("127.0.0.1:abc")]
    public void TryExtractPort_非法输入应返回False(string? address)
    {
        Assert.False(PortProcessResolver.TryExtractPort(address, out _));
    }

    [Fact]
    public async Task FindListeningPidAsync_对真实监听端口_应返回当前进程PID()
    {
        // 端到端校验：真的起一个监听，再通过 netstat 反查回来。
        // 这能同时覆盖"命令可用""输出解析""PID 归属"三件事。
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var pid = await PortProcessResolver.FindListeningPidAsync(port, CancellationToken.None);

            Assert.NotNull(pid);
            Assert.Equal(Environment.ProcessId, pid!.Value);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task FindListeningPidAsync_对未监听端口_应返回Null()
    {
        // 先占后放：确保这个端口在本进程周期内不会真正被监听。
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var pid = await PortProcessResolver.FindListeningPidAsync(port, CancellationToken.None);

        Assert.Null(pid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public async Task FindListeningPidAsync_端口非法_应返回Null而非抛异常(int port)
    {
        Assert.Null(await PortProcessResolver.FindListeningPidAsync(port, CancellationToken.None));
    }
}
