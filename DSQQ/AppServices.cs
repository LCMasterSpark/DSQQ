using System.IO;
using DSQQ.Models;
using DSQQ.Services;

namespace DSQQ;

// 应用级服务宿主（组合根）。
// 为什么不直接上依赖注入容器：本程序的依赖图很浅（配置 → 编排器 → 界面），
// 引入 Microsoft.Extensions.DependencyInjection 的收益暂时低于它带来的间接层。
// 这里用一个显式的宿主类替代，等到依赖数量明显上升（P4/P5 接入两个 API 客户端）时再切换。
// 关键职责是配置变更后的重建：用户在设置页改完路径/端口后，
// ServiceOrchestrator 持有的配置必须同步更新，否则"启动"仍会用旧路径。
public sealed class AppServices
{
    private readonly object _gate = new();
    private AppConfig _config;
    private ServiceOrchestrator _orchestrator;

    // 初始化服务宿主。
    // 参数 config：应用配置；为 null 时使用默认值。
    public AppServices(AppConfig? config = null)
    {
        _config = config ?? AppConfig.CreateDefault();
        _orchestrator = new ServiceOrchestrator(_config);
    }

    // 配置发生变化时触发（保存设置后）。
    public event EventHandler<AppConfig>? ConfigChanged;

    // 当前配置的只读快照。
    public AppConfig Config
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    // 当前端点。
    public ServiceEndpoints Endpoints => ServiceEndpoints.FromConfig(Config);

    // 当前编排器（每次配置变更后都是新实例）。
    public ServiceOrchestrator Orchestrator
    {
        get
        {
            lock (_gate)
            {
                return _orchestrator;
            }
        }
    }

    // 应用新的配置：写入磁盘并重建编排器。
    // 参数 config：新配置。
    // 异常 ArgumentNullException：为 null。
    // 异常 IOException：配置写入失败（磁盘只读、被占用等）。
    // 采用"先落盘再切换"的顺序：写盘失败时内存与磁盘都保持旧值，不会出现
    // "界面显示已改、实际启动仍用旧路径"的错位。
    public void ApplyConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        AppConfigStore.Save(config);

        lock (_gate)
        {
            _config = config;
            _orchestrator = new ServiceOrchestrator(config);
        }

        ConfigChanged?.Invoke(this, config);
    }

    // 读取默认路径下的配置；失败时退化为默认值，并给出提示。
    // 参数 error：读取失败原因；成功时为 null。
    // 返回：配置实例（永不为 null）。
    public static AppConfig LoadOrDefault(out string? error)
    {
        var loaded = AppConfigStore.TryLoad(out error);
        var config = loaded ?? AppConfig.CreateDefault();

        // 自动补齐缺失的组件路径。
        //
        // 配置里没有值、或指向的位置已经无效（别人拿到程序、或用户挪了目录）时，
        // 按「便携布局 → 程序目录 → 常见位置」搜一遍。目的是让别人下载后
        // 不必先手动填两个路径才能用。
        //
        // 只补空缺，不覆盖用户明确指定过的有效路径。
        var found = ServiceLocator.Discover(
            AppContext.BaseDirectory,
            config.SnowLuma.InstallDirectory,
            config.Bridge.Directory);

        if (found.SnowLuma is not null)
        {
            config.SnowLuma.InstallDirectory = found.SnowLuma;
        }

        if (found.Bridge is not null)
        {
            config.Bridge.Directory = found.Bridge;
        }

        LastDiscovery = found;

        return config;
    }

    // 最近一次自动发现的结果，供界面解释"路径是怎么来的"。
    // 在 LoadOrDefault 里填充；未执行过发现时为 null。
    public static ServiceLocator.Result? LastDiscovery { get; private set; }

    // 配置文件路径（供界面显示，便于用户手工编辑）。
    public static string ConfigFilePath => AppConfigStore.DefaultPath;

    // 桥接日志文件路径。
    public string BridgeLogPath => Path.Combine(Config.Bridge.Directory, "state", "bridge.log");

    // 桥接活动日志路径。
    public string BridgeActivityLogPath => Path.Combine(Config.Bridge.Directory, "state", "qq-activity.log");
}
