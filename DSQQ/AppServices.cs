using System.IO;
using DSQQ.Models;
using DSQQ.Services;

namespace DSQQ;

/// <summary>
/// 应用级服务宿主（组合根）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不直接上依赖注入容器：本程序的依赖图很浅（配置 → 编排器 → 界面），
/// 引入 <c>Microsoft.Extensions.DependencyInjection</c> 的收益暂时低于它带来的间接层。
/// 这里用一个显式的宿主类替代，等到依赖数量明显上升（P4/P5 接入两个 API 客户端）时再切换。
/// </para>
/// <para>
/// 关键职责是**配置变更后的重建**：用户在设置页改完路径/端口后，
/// <see cref="ServiceOrchestrator"/> 持有的配置必须同步更新，否则"启动"仍会用旧路径。
/// </para>
/// </remarks>
public sealed class AppServices
{
    private readonly object _gate = new();
    private AppConfig _config;
    private ServiceOrchestrator _orchestrator;

    /// <summary>
    /// 初始化服务宿主。
    /// </summary>
    /// <param name="config">应用配置；为 <c>null</c> 时使用默认值。</param>
    public AppServices(AppConfig? config = null)
    {
        _config = config ?? AppConfig.CreateDefault();
        _orchestrator = new ServiceOrchestrator(_config);
    }

    /// <summary>配置发生变化时触发（保存设置后）。</summary>
    public event EventHandler<AppConfig>? ConfigChanged;

    /// <summary>当前配置的只读快照。</summary>
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

    /// <summary>当前端点。</summary>
    public ServiceEndpoints Endpoints => ServiceEndpoints.FromConfig(Config);

    /// <summary>当前编排器（每次配置变更后都是新实例）。</summary>
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

    /// <summary>
    /// 应用新的配置：写入磁盘并重建编排器。
    /// </summary>
    /// <param name="config">新配置。</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 <c>null</c>。</exception>
    /// <exception cref="IOException">配置写入失败（磁盘只读、被占用等）。</exception>
    /// <remarks>
    /// 采用"先落盘再切换"的顺序：写盘失败时内存与磁盘都保持旧值，不会出现
    /// "界面显示已改、实际启动仍用旧路径"的错位。
    /// </remarks>
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

    /// <summary>
    /// 读取默认路径下的配置；失败时退化为默认值，并给出提示。
    /// </summary>
    /// <param name="error">读取失败原因；成功时为 <c>null</c>。</param>
    /// <returns>配置实例（永不为 <c>null</c>）。</returns>
    public static AppConfig LoadOrDefault(out string? error)
    {
        var loaded = AppConfigStore.TryLoad(out error);
        return loaded ?? AppConfig.CreateDefault();
    }

    /// <summary>配置文件路径（供界面显示，便于用户手工编辑）。</summary>
    public static string ConfigFilePath => AppConfigStore.DefaultPath;

    /// <summary>桥接日志文件路径。</summary>
    public string BridgeLogPath => Path.Combine(Config.Bridge.Directory, "state", "bridge.log");

    /// <summary>桥接活动日志路径。</summary>
    public string BridgeActivityLogPath => Path.Combine(Config.Bridge.Directory, "state", "qq-activity.log");
}
