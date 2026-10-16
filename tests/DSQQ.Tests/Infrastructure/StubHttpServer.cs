using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DSQQ.Tests.Infrastructure;

// 极简 HTTP 桩，供"真实 HTTP 往返"类测试使用。
//
// 为什么用 TcpListener 而不是 HttpListener：后者在 Windows 上需要 URL 预留
// （通常要管理员权限），测试会因为权限而失败。TcpListener 绑随机端口没有任何前提。
//
// 为什么不用更省事的写法直接 mock HttpClient：这套测试要验证的恰恰是
// **客户端真的把该带的东西带出去了**（Authorization 头、请求体的字段名），
// 而那只有在真发一次请求时才看得见。
public sealed class StubHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    // 创建一个监听随机端口的桩。端口 0 = 让操作系统分配空闲端口，测试之间不会撞车。
    public StubHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri($"http://127.0.0.1:{port}");

        _loop = Task.Run(AcceptLoopAsync);
    }

    // 桩的基地址，交给被测客户端当 BaseAddress。
    public Uri BaseUri { get; }

    // 所有响应的状态码。
    public int StatusCode { get; init; } = 200;

    // 未命中 Routes 时使用的响应体。
    public string ResponseBody { get; init; } = """{"version":"stub"}""";

    // 按路径定制的响应；未命中的路径回落到 ResponseBody。
    public Dictionary<string, string> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

    // 最近一次请求里 Authorization 头的值；没有该头时为 null。
    public string? LastAuthorization { get; private set; }

    // 最近一次请求的正文（按 Content-Length 读全）；GET 时为 null。
    public string? LastRequestBody { get; private set; }

    // 收到过的请求路径（不含查询串），按顺序。
    public List<string> RequestPaths { get; } = [];

    // 最近一次请求的原始文本（含请求行、头、正文），仅供排查用。
    public string? LastRawRequest { get; private set; }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            using (connection)
            {
                await HandleAsync(connection);
            }
        }
    }

    private async Task HandleAsync(TcpClient connection)
    {
        // 每次请求前重置：否则"清除凭据后不再带 Authorization"这类断言
        // 会读到上一次请求留下的值，测试因错误的原因通过。
        LastAuthorization = null;
        LastRequestBody = null;

        var stream = connection.GetStream();
        var buffer = new byte[8192];
        var request = new StringBuilder();

        // 先把头部读完（以空行结束），再按 Content-Length 把正文读全 ——
        // 断言 POST 体形状的用例需要正文，只读头部是不够的。
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                break;
            }

            request.Append(Encoding.UTF8.GetString(buffer, 0, read));
            headerEnd = request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }

        var text = request.ToString();
        var path = string.Empty;
        var contentLength = 0;
        var chunked = false;

        foreach (var line in text.Split("\r\n"))
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                LastAuthorization = line["Authorization:".Length..].Trim();
            }
            else if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
            }
            else if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
            {
                chunked = line["Transfer-Encoding:".Length..].Contains("chunked", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("GET ", StringComparison.Ordinal) || line.StartsWith("POST ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 2)
                {
                    // 去掉查询串，路由按路径匹配。
                    path = parts[1].Split('?')[0];
                }
            }
        }

        RequestPaths.Add(path);
        LastRawRequest = text;

        if (headerEnd >= 0 && chunked)
        {
            // 分块编码必须单独处理：PostAsJsonAsync 走的就是这条路（只带
            // Transfer-Encoding 而没有 Content-Length）。桩要是只认 Content-Length，
            // 这些请求的正文就永远读不到，而断言正文的用例会以看不懂的方式失败。
            var raw = new StringBuilder(text[(headerEnd + 4)..]);

            while (!IsCompleteChunkedBody(raw.ToString()))
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0)
                {
                    break;
                }

                raw.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            LastRequestBody = DecodeChunkedBody(raw.ToString());
        }
        else if (headerEnd >= 0 && contentLength > 0)
        {
            var bodySoFar = text[(headerEnd + 4)..];
            var remaining = contentLength - bodySoFar.Length;
            var extra = new StringBuilder(bodySoFar);

            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0)
                {
                    break;
                }

                extra.Append(Encoding.UTF8.GetString(buffer, 0, read));
                remaining -= read;
            }

            LastRequestBody = extra.ToString();
        }

        var payload = Routes.TryGetValue(path, out var routed) ? routed : ResponseBody;

        var body = Encoding.UTF8.GetBytes(payload);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {StatusCode} OK\r\n"
            + "Content-Type: application/json\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Connection: close\r\n\r\n");

        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Stop();
        _shutdown.Dispose();

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 关闭时的取消异常无需处理。
        }
    }

    // 分块正文是否已经收全：终止块是长度 0 的那一块。
    private static bool IsCompleteChunkedBody(string raw)
        => raw.EndsWith("0\r\n\r\n", StringComparison.Ordinal)
            || raw.Contains("\r\n0\r\n\r\n", StringComparison.Ordinal);

    // 把分块编码解回原始正文。
    // 格式：`<十六进制长度>\r\n<数据>\r\n` 重复，最后以 `0\r\n\r\n` 结束。
    // 数据没收全时尽力返回已解出的部分 —— 那也比返回 null 有用。
    private static string DecodeChunkedBody(string raw)
    {
        var result = new StringBuilder();
        var index = 0;

        while (index < raw.Length)
        {
            var lineEnd = raw.IndexOf("\r\n", index, StringComparison.Ordinal);
            if (lineEnd < 0)
            {
                break;
            }

            // 块长度后面可能跟扩展（`1a;name=value`），取分号前的部分。
            var sizeText = raw[index..lineEnd].Split(';')[0].Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size == 0)
            {
                break;
            }

            var start = lineEnd + 2;
            if (start + size > raw.Length)
            {
                result.Append(raw[start..]);
                break;
            }

            result.Append(raw, start, size);

            // 跳过数据与其后的 \r\n。
            index = start + size + 2;
        }

        return result.ToString();
    }
}
