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
    /// <summary>
    /// 按配置解析三个服务的端点。
    /// </summary>
    /// <param name="config">应用配置。</param>
    /// <returns>解析结果。端口一律绑定到回环地址，本程序不会连接外部主机。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 <c>null</c>。</exception>
    /// <remarks>
    /// 设计说明：
    /// <list type="bullet">
    /// <item>统一走 <c>127.0.0.1</c> 而非 <c>localhost</c>：本机曾出现 <c>::1</c> 未监听而
    /// <c>127.0.0.1</c> 监听的情况，写死 IPv4 可避免解析顺序带来的偶发连接失败；</item>
    /// <item>端口全部来自配置，因为 SnowLuma 端口会被顺延、DSH 桌面版端口也不是文档默认值。</item>
    /// </list>
    /// </remarks>
    public static ServiceEndpoints FromConfig(Models.AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ServiceEndpoints(
            BuildLoopbackUri(config.SnowLuma.PreferredWebUiPort),
            BuildLoopbackUri(config.Bridge.ConsolePort),
            BuildLoopbackUri(config.Dsh.Port));
    }

    /// <summary>
    /// 生成 <c>http://127.0.0.1:{port}</c> 形式的基地址。
    /// </summary>
    /// <param name="port">端口号。</param>
    /// <exception cref="ArgumentOutOfRangeException">端口不在 1~65535 范围内。</exception>
    public static Uri BuildLoopbackUri(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "端口必须介于 1 与 65535 之间。");
        }

        return new Uri($"http://127.0.0.1:{port}");
    }
}
