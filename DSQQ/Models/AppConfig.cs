using System.IO;
using System.Text.Json.Serialization;

namespace DSQQ.Models;

// 应用配置根对象（持久化到 %APPDATA%\DSQQ\config.json）。
// 设计取舍：
// 只存"用户意图"（路径、端口、开关），不存任何派生状态；
// 所有字段都给安全默认值，配置文件损坏或缺失时程序仍可启动（CreateDefault）；
// 使用 JsonExtensionData 保留未知字段，避免新版本写出的配置被旧版本读入后悄悄丢弃。
public sealed class AppConfig
{
    // 配置结构版本号，供将来做迁移使用。
    public int SchemaVersion { get; set; } = 1;

    // SnowLuma 设置。
    public SnowLumaSettings SnowLuma { get; set; } = new();

    // qq-bridge 设置。
    public BridgeSettings Bridge { get; set; } = new();

    // DSH 设置。
    public DshSettings Dsh { get; set; } = new();

    // 启动行为。
    public StartupSettings Startup { get; set; } = new();

    // 监控与日志。
    public MonitoringSettings Monitoring { get; set; } = new();

    // 机器人账号。
    public OwnerSettings Owner { get; set; } = new();

    // 未识别的 JSON 字段，原样保留以兼容新旧版本。
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    // 生成默认配置（默认路径对应本机实际部署位置，可在设置页改写）。
    public static AppConfig CreateDefault() => new();
}

// SnowLuma（第三方 QQ 网关）设置。
public sealed class SnowLumaSettings
{
    // 安装目录（其下的 config\ 与 logs\ 是主要数据源）。
    //
    // 默认为空 = "未指定"，由 ServiceLocator 在启动时自动搜索并填入。
    // 这里刻意不写死开发者本机路径（原先的 D:\SnowLuma）：别人装到别处时
    // 那个默认值只会误导，而且会让"零配置"变成"先改配置"。
    public string InstallDirectory { get; set; } = string.Empty;

    // 启动脚本文件名（相对安装目录）。
    public string LauncherFileName { get; set; } = "launcher.bat";

    // 偏好的 WebUI 端口。若被占用，SnowLuma 会顺延到 5100 等，程序必须实际探测。
    public int PreferredWebUiPort { get; set; } = 5099;

    // 从偏好端口起连续探测的个数，用于应对端口顺延。
    public int PortProbeCount { get; set; } = 10;

    // 是否向 launcher.bat 注入 SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD。
    // 默认关闭：协议同意与扫码登录必须由用户本人完成（EULA §5.4 禁止自动化部署其专有组件）。
    public bool InjectBootstrapPassword { get; set; }

    // 启动密码；仅在 InjectBootstrapPassword 为真时使用。
    public string? BootstrapPassword { get; set; }

    // WebUI 密码的 DPAPI 密文；仅本机当前用户可解密。
    public string? WebUiPasswordProtected { get; set; }

    // WebUI 会话 token 的 DPAPI 密文，用于免重复登录。
    // 早期版本存的是 Cookie，但那个 Cookie 只对 /avatar 有效、调 API 一律 401，
    // 所以改名成 token —— 老值本来就没起过作用，不存在迁移问题。
    public string? SessionTokenProtected { get; set; }
}

// 机器人账号相关设置。
public sealed class OwnerSettings
{
    // 管理员 QQ 号。
    public long? Qq { get; set; }
}

// qq-bridge（QQ ↔ DSH 桥接）设置。
public sealed class BridgeSettings
{
    // 桥接项目根目录。默认为空 = 交给自动发现，理由同上。
    public string Directory { get; set; } = string.Empty;

    // 桥接控制台端口，对应桥接 config.json 的 consolePort。
    public int ConsolePort { get; set; } = 3100;

    // 桥接入口脚本的相对路径。
    public string EntryRelativePath { get; set; } = Path.Combine("src", "bridge.js");

    // node.exe 路径；留空则自动探测（优先复用 DSH 运行时自带的 Node）。
    public string? NodeExecutablePath { get; set; }
}

// DSH（DeepSeek Harness）设置。
public sealed class DshSettings
{
    // DSH 主目录（默认 %USERPROFILE%\.dsh）。
    public string HomeDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

    // DSH Web API 端口（桌面版实测 19387，并非文档里的 3080）。
    public int Port { get; set; } = 19387;
}

// 启动行为。
public sealed class StartupSettings
{
    // 一键启动时是否拉起 SnowLuma。
    public bool LaunchSnowLuma { get; set; } = true;

    // 一键启动时是否拉起 qq-bridge。
    public bool LaunchBridge { get; set; } = true;

    // 关闭主窗口时最小化到托盘而非退出。
    public bool MinimizeToTrayOnClose { get; set; } = true;

    // 程序启动后是否自动执行一次"一键启动"。
    public bool AutoStartOnLaunch { get; set; }
}

// 监控与日志。
public sealed class MonitoringSettings
{
    // 状态轮询间隔（毫秒）。
    public int PollIntervalMs { get; set; } = 2000;

    // 文件跟随去抖延迟（毫秒）。
    public int FileWatchDebounceMs { get; set; } = 300;

    // 日志视图初始加载的尾部行数。
    public int TailLines { get; set; } = 2000;

    // 单次日志读取字节上限，避免大文件占满内存。
    public int MaxReadBytesPerTick { get; set; } = 512 * 1024;

    // 触发托盘告警的关键字（大小写不敏感）。
    public List<string> AlertKeywords { get; set; } =
    [
        "agent 处理出错",
        "token 自愈失败",
        "receive path stale",
        "process enumeration timed out",
    ];
}
