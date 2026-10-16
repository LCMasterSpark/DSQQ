using System.IO;

namespace DSQQ.Services;

// 自动发现 SnowLuma 与 qq-bridge 的安装位置。
//
// 为什么需要它：
// 默认配置里的路径是开发者本机的（D:\SnowLuma），别人拿到程序后必须手动改，
// 否则一键启动直接失败。而"下载一个程序就能用"的前提，是尽量不让人手填路径。
//
// 搜索顺序（先命中先用）：
//   1) 便携布局 {程序目录}\services\{snowluma,qq-bridge}
//      —— 将来把三个组件打包分发时用这个结构，解压即可用
//   2) 程序目录内 {程序目录}\{SnowLuma,qq-bridge}
//   3) 程序目录的父级
//   4) 常见安装位置（各盘根、用户目录、桌面、下载）
//
// 每个候选都用**特征文件**验证，不只看目录名：
// 否则一个恰好同名的空目录会被当成有效安装，用户会看到"目录存在但启动失败"。
public static class ServiceLocator
{
    // SnowLuma 的特征文件：入口脚本。
    private static readonly string[] SnowLumaMarkers = ["index.mjs"];

    // qq-bridge 的特征文件：入口脚本。
    private static readonly string[] BridgeMarkers = [Path.Combine("src", "bridge.js")];

    // 目录名的大小写与连字符变体 —— 不同人解压时可能改过名。
    private static readonly string[] SnowLumaNames = ["SnowLuma", "snowluma", "snow-luma"];

    private static readonly string[] BridgeNames = ["qq-bridge", "qq-bridge-custom", "QQBridge", "qqbridge"];

    // 一次发现的结果。
    // 参数 SnowLuma：找到的 SnowLuma 目录；没找到为 null。
    // 参数 Bridge：找到的 qq-bridge 目录；没找到为 null。
    // 参数 Searched：搜索过的候选目录（用于在界面上解释"我找过哪里"）。
    public sealed record Result(string? SnowLuma, string? Bridge, IReadOnlyList<string> Searched);

    // 判断目录是否为有效的 SnowLuma 安装。
    public static bool IsSnowLuma(string? directory) => HasAllMarkers(directory, SnowLumaMarkers);

    // 判断目录是否为有效的 qq-bridge 目录。
    public static bool IsBridge(string? directory) => HasAllMarkers(directory, BridgeMarkers);

    // 执行发现。
    //
    // 参数 appDirectory：程序所在目录（通常是 AppContext.BaseDirectory）。
    // 参数 currentSnowLuma / currentBridge：配置里已有的值。
    //   已经有**有效**值时直接沿用，不搜索 —— 用户手动指定过的位置优先，
    //   自动发现只用来补空缺，不该覆盖用户的明确选择。
    public static Result Discover(
        string appDirectory,
        string? currentSnowLuma,
        string? currentBridge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);

        var searched = new List<string>();

        var snowLuma = IsSnowLuma(currentSnowLuma)
            ? currentSnowLuma
            : Search(appDirectory, SnowLumaNames, SnowLumaMarkers, searched);

        var bridge = IsBridge(currentBridge)
            ? currentBridge
            : Search(appDirectory, BridgeNames, BridgeMarkers, searched);

        return new Result(snowLuma, bridge, searched);
    }

    // 生成候选目录并逐一验证；返回第一个命中的。
    private static string? Search(
        string appDirectory,
        string[] names,
        string[] markers,
        List<string> searched)
    {
        foreach (var candidate in CandidateDirectories(appDirectory, names))
        {
            searched.Add(candidate);

            if (HasAllMarkers(candidate, markers))
            {
                // 返回的是**候选名**拼出的路径，未必等于磁盘上的原始写法
                //（例如磁盘上是 snowluma、候选是 SnowLuma）。Windows 文件系统
                // 不区分大小写，两者指向同一目录，所以直接可用 —— 想显示磁盘原名
                // 得额外枚举父目录，那点收益不值得。
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    // 按优先级展开候选目录。
    private static IEnumerable<string> CandidateDirectories(string appDirectory, string[] names)
    {
        var normalizedApp = Path.GetFullPath(appDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var name in names)
        {
            // 1) 便携布局：三个组件放在一起，解压即用
            yield return Path.Combine(normalizedApp, "services", name);

            // 2) 程序目录内 / 同级
            yield return Path.Combine(normalizedApp, name);
        }

        // 3) 程序目录的父级（DSQQ 放在某个总目录下的常见情况）
        var parent = Path.GetDirectoryName(normalizedApp);
        if (!string.IsNullOrEmpty(parent))
        {
            foreach (var name in names)
            {
                yield return Path.Combine(parent, name);
            }
        }

        // 4) 常见位置
        foreach (var root in CommonRoots())
        {
            foreach (var name in names)
            {
                yield return Path.Combine(root, name);
            }
        }
    }

    // 常见安装根目录：各固定盘根 + 用户目录 + 桌面 + 下载。
    //
    // 只列举**低成本**的位置：目录存在性检查很快，但遍历整个磁盘会很慢，
    // 所以这里不做全盘递归搜索。
    private static IEnumerable<string> CommonRoots()
    {
        // 各个固定磁盘的根（C: D: E: …）
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive is { IsReady: true, DriveType: DriveType.Fixed })
            {
                yield return drive.RootDirectory.FullName;
            }
        }

        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(user))
        {
            yield return user;

            foreach (var sub in new[] { "Desktop", "Downloads", "Documents", "software", "tools", "apps" })
            {
                yield return Path.Combine(user, sub);
            }
        }
    }

    // 目录存在且含全部特征文件。
    private static bool HasAllMarkers(string? directory, string[] markers)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        foreach (var marker in markers)
        {
            if (!File.Exists(Path.Combine(directory, marker)))
            {
                return false;
            }
        }

        return true;
    }
}
