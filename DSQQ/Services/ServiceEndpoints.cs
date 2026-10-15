namespace DSQQ.Services;

/// <summary>
/// 本机三个服务的端点集合。
/// </summary>
/// <param name="SnowLumaWebUi">SnowLuma WebUI 基地址，例如 <c>http://127.0.0.1:5099</c>。</param>
/// <param name="BridgeConsole">qq-bridge 控制台基地址，例如 <c>http://127.0.0.1:3100</c>。</param>
/// <param name="DshWeb">DSH Web API 基地址，例如 <c>http://127.0.0.1:19387</c>。</param>
public readonly record struct ServiceEndpoints(
    Uri SnowLumaWebUi,
    Uri BridgeConsole,
    Uri DshWeb)
{
    /// <summary>按配置解析三个服务的端点。</summary>
    /// <param name="config">应用配置。</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 <c>null</c>。</exception>
    /// <remarks>
    /// 一律绑定 127.0.0.1 而非 localhost：本机出现过 ::1 未监听而 127.0.0.1 监听的情况，
    /// 写死 IPv4 可避免解析顺序导致的连接失败。
    /// </remarks>
    public static ServiceEndpoints FromConfig(Models.AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ServiceEndpoints(
            BuildLoopbackUri(config.SnowLuma.PreferredWebUiPort),
            BuildLoopbackUri(config.Bridge.ConsolePort),
            BuildLoopbackUri(config.Dsh.Port));
    }

    /// <summary>生成 http://127.0.0.1:{port} 形式的基地址。</summary>
    /// <param name="port">端口号（1~65535）。</param>
    /// <exception cref="ArgumentOutOfRangeException">端口越界。</exception>
    public static Uri BuildLoopbackUri(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "端口必须介于 1 与 65535 之间。");
        }

        return new Uri($"http://127.0.0.1:{port}");
    }
}
