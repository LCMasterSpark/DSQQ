using System.IO;
using System.Text.Json.Serialization;

namespace DSQQ.Models;

/// <summary>
/// 应用配置根对象（持久化到 %APPDATA%\DSQQ\config.json）。
/// </summary>
/// <remarks>
/// 设计取舍：
/// <list type="bullet">
/// <item>只存"用户意图"（路径、端口、开关），不存任何派生状态；</item>
/// <item>所有字段都给安全默认值，配置文件损坏或缺失时程序仍可启动（<see cref="CreateDefault"/>）；</item>
/// <item>使用 <see cref="JsonExtensionData"/> 保留未知字段，避免新版本写出的配置被旧版本读入后悄悄丢弃。</item>
/// </list>
/// </remarks>
public sealed class AppConfig
{
    /// <summary>配置结构版本号，供将来做迁移使用。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>SnowLuma 相关设置。</summary>
    public SnowLumaSettings SnowLuma { get; set; } = new();

    /// <summary>qq-bridge 相关设置。</summary>
    public BridgeSettings Bridge { get; set; } = new();

    /// <summary>DSH（DeepSeek Harness）相关设置。</summary>
    public DshSettings Dsh { get; set; } = new();

    /// <summary>启动行为设置。</summary>
    public StartupSettings Startup { get; set; } = new();

    /// <summary>监控与日志设置。</summary>
    public MonitoringSettings Monitoring { get; set; } = new();

    /// <summary>机器人账号设置。</summary>
    public OwnerSettings Owner { get; set; } = new();

    /// <summary>未识别的 JSON 字段，原样保留以便向后兼容。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>
    /// 生成一套安全默认配置。
    /// </summary>
    /// <remarks>
    /// 默认值取自本项目实际部署环境（<c>D:\SnowLuma</c> 与 <c>D:\qq-bridge</c>），
    /// 但用户可在设置页改写；路径不存在时程序只提示，不崩溃。
    /// </remarks>
    public static AppConfig CreateDefault() => new();
}

/// <summary>SnowLuma（第三方 QQ 网关）设置。</summary>
public sealed class SnowLumaSettings
{
    /// <summary>安装目录，例如 <c>D:\SnowLuma</c>。其下的 <c>config\</c> 与 <c>logs\</c> 是主要数据源。</summary>
    public string InstallDirectory { get; set; } = @"D:\SnowLuma";

    /// <summary>启动脚本文件名（相对安装目录）。</summary>
    public string LauncherFileName { get; set; } = "launcher.bat";

    /// <summary>偏好的 WebUI 端口。若被占用，SnowLuma 会顺延到 5100 等，程序必须实际探测。</summary>
    public int PreferredWebUiPort { get; set; } = 5099;

    /// <summary>端口探测数量（从偏好端口起连续探测），用于应对端口顺延。</summary>
    public int PortProbeCount { get; set; } = 10;

    /// <summary>
    /// 是否自动对 <c>launcher.bat</c> 子进程设置 <c>SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 合规边界：SnowLuma EULA §5.4 禁止"通过自动化脚本部署其专有组件"，
    /// 且协议同意与扫码登录必须由用户本人完成。因此本程序**默认不做任何自动化登录**；
    /// 该开关仅供用户明确要求"由程序代为注入启动密码"时使用，默认关闭。
    /// </remarks>
    public bool InjectBootstrapPassword { get; set; }

    /// <summary>启动密码；仅在 <see cref="InjectBootstrapPassword"/> 为真时使用。</summary>
    public string? BootstrapPassword { get; set; }

    /// <summary>WebUI 密码的 DPAPI 密文（含 <c>dpapi:</c> 前缀）。</summary>
    /// <remarks>只在本机当前用户下可解密；换机器或换用户后读不出，会提示重新输入。</remarks>
    public string? WebUiPasswordProtected { get; set; }

    /// <summary>WebUI 会话 Cookie 的 DPAPI 密文，用于免重复登录。</summary>
    public string? SessionCookieProtected { get; set; }
}

/// <summary>机器人账号相关设置。</summary>
public sealed class OwnerSettings
{
    /// <summary>管理员（主人）QQ 号；用于白名单默认值与界面展示。</summary>
    public long? Qq { get; set; }
}

/// <summary>qq-bridge（QQ ↔ DSH 桥接）设置。</summary>
public sealed class BridgeSettings
{
    /// <summary>桥接项目根目录，例如 <c>D:\qq-bridge</c>。</summary>
    public string Directory { get; set; } = @"D:\qq-bridge";

    /// <summary>控制台（HTTP API）端口，对应桥接 config.json 的 <c>consolePort</c>。</summary>
    public int ConsolePort { get; set; } = 3100;

    /// <summary>桥接入口脚本的相对路径。</summary>
    public string EntryRelativePath { get; set; } = Path.Combine("src", "bridge.js");

    /// <summary>
    /// 使用的 node.exe 绝对路径；留空表示自动探测（优先复用 DSH 运行时自带的 node）。
    /// </summary>
    /// <remarks>桥接要求 Node ≥ 22.13；DSH 运行时通常已带符合要求的版本，复用可避免在 C 盘外额外安装。</remarks>
    public string? NodeExecutablePath { get; set; }
}

/// <summary>DSH（DeepSeek Harness）设置。</summary>
public sealed class DshSettings
{
    /// <summary>DSH 主目录（默认 <c>%USERPROFILE%\.dsh</c>）。</summary>
    public string HomeDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

    /// <summary>
    /// Web API 端口。
    /// </summary>
    /// <remarks>注意：DSH 桌面版实测为 19387，而非文档里的 3080 —— 因此该值必须可配置并支持探测。</remarks>
    public int Port { get; set; } = 19387;
}

/// <summary>启动行为设置。</summary>
public sealed class StartupSettings
{
    /// <summary>一键启动时是否拉起 SnowLuma。</summary>
    public bool LaunchSnowLuma { get; set; } = true;

    /// <summary>一键启动时是否拉起 qq-bridge。</summary>
    public bool LaunchBridge { get; set; } = true;

    /// <summary>关闭主窗口时最小化到托盘而非退出。</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>程序启动后是否自动执行一次"一键启动"。</summary>
    public bool AutoStartOnLaunch { get; set; }
}

/// <summary>监控与日志设置。</summary>
public sealed class MonitoringSettings
{
    /// <summary>状态轮询间隔（毫秒）。</summary>
    public int PollIntervalMs { get; set; } = 2000;

    /// <summary>文件跟随去抖延迟（毫秒）。</summary>
    public int FileWatchDebounceMs { get; set; } = 300;

    /// <summary>日志视图初始加载的尾部行数。</summary>
    public int TailLines { get; set; } = 2000;

    /// <summary>单次日志读取的最大字节数，防止超大文件把内存打满。</summary>
    public int MaxReadBytesPerTick { get; set; } = 512 * 1024;

    /// <summary>触发托盘告警的关键字（大小写不敏感）。</summary>
    public List<string> AlertKeywords { get; set; } =
    [
        "agent 处理出错",
        "token 自愈失败",
        "receive path stale",
        "process enumeration timed out",
    ];
}
