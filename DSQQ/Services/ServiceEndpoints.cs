namespace DSQQ.Services;

// 本机三个服务的端点集合。
// 参数 SnowLumaWebUi：SnowLuma WebUI 基地址，例如 http://127.0.0.1:5099。
// 参数 BridgeConsole：qq-bridge 控制台基地址，例如 http://127.0.0.1:3100。
// 参数 DshWeb：DSH Web API 基地址，例如 http://127.0.0.1:19387。
public readonly record struct ServiceEndpoints(
    Uri SnowLumaWebUi,
    Uri BridgeConsole,
    Uri DshWeb)
{
    // 按配置解析三个服务的端点。
    // 参数 config：应用配置。
    // 异常 ArgumentNullException：为 null。
    // 一律绑定 127.0.0.1 而非 localhost：本机出现过 ::1 未监听而 127.0.0.1 监听的情况，
    // 写死 IPv4 可避免解析顺序导致的连接失败。
    public static ServiceEndpoints FromConfig(Models.AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ServiceEndpoints(
            BuildLoopbackUri(config.SnowLuma.PreferredWebUiPort),
            BuildLoopbackUri(config.Bridge.ConsolePort),
            BuildLoopbackUri(config.Dsh.Port));
    }

    // 生成 http://127.0.0.1:{port} 形式的基地址。
    // 参数 port：端口号（1~65535）。
    // 异常 ArgumentOutOfRangeException：端口越界。
    public static Uri BuildLoopbackUri(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "端口必须介于 1 与 65535 之间。");
        }

        return new Uri($"http://127.0.0.1:{port}");
    }
}
