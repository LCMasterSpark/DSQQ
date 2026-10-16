using System.IO;
using DSQQ.Models;

namespace DSQQ.Services;

// 首次运行的体检：把"现在能不能用、缺什么、下一步做什么"一次性算清楚。
//
// 为什么需要它：这套东西要串起 DSH / SnowLuma / qq-bridge 三层，
// 任何一层没就绪都会表现为"机器人不回话"，而用户看到的只是一个静默的界面。
// 与其让他猜，不如把每一环的状态和补救动作直接摆出来。
//
// 纯逻辑（只做文件与端口探测），便于单元测试。
public static class FirstRunCheck
{
    // 一个检查项。
    // 参数 Name：环节名。
    // 参数 State：就绪 / 待处理 / 缺失。
    // 参数 Detail：现状说明（能填路径就填路径，用户要拿去核对）。
    // 参数 NextStep：该做什么 —— 缺失项必须给，否则用户只能干瞪眼。
    public sealed record Item(string Name, CheckState State, string Detail, string? NextStep);

    public enum CheckState
    {
        // 已就绪，不用管。
        Ready,

        // 存在但还没就绪（例如端口尚未开始监听）。
        Pending,

        // 找不到，必须处理。
        Missing,
    }

    // 体检结果。
    // 参数 Items：各环节状态。
    // 参数 ReadyToChat：全部就绪、可以开始聊天。
    public sealed record Report(IReadOnlyList<Item> Items, bool ReadyToChat)
    {
        // 还没就绪的项（界面按这个列表引导）。
        public IEnumerable<Item> Blocking => Items.Where(i => i.State != CheckState.Ready);
    }

    // 探测用的外部事实。由调用方注入，测试里可以伪造。
    // 参数 DshHomeExists：DSH 数据目录是否存在。
    // 参数 DshPortOpen：DSH 的 API 端口是否在监听。
    // 参数 SnowLumaDir：找到的 SnowLuma 目录；null 表示没找到。
    // 参数 BridgeDir：找到的 qq-bridge 目录；null 表示没找到。
    // 参数 SnowLumaWebUiOpen：SnowLuma WebUI 端口是否在监听。
    // 参数 OneBotPortOpen：OneBot 端口是否在监听。
    // 参数 BridgeConsoleOpen：桥接控制台端口是否在监听。
    public sealed record Environment(
        bool DshHomeExists,
        bool DshPortOpen,
        string? SnowLumaDir,
        string? BridgeDir,
        bool SnowLumaWebUiOpen,
        bool OneBotPortOpen,
        bool BridgeConsoleOpen);

    // 执行体检。
    //
    // 参数 config：当前配置（用来取端口与 DSH 主目录）。
    // 参数 environment：探测到的外部事实。
    // 参数 bridgeStatus：桥接的实时状态；取不到时传 null。
    //   管理员 QQ 与白名单**以桥接为准** —— 它们是桥接的配置，
    //   不是 DSQQ 本地的，只有 bridgeStatus 能反映真实生效值。
    public static Report Inspect(
        AppConfig config,
        Environment environment,
        BridgeStatus? bridgeStatus = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(environment);

        var items = new List<Item>();

        // ── 1. DSH：AI 能力的来源，最底层的前提 ──
        if (!environment.DshHomeExists)
        {
            items.Add(new Item(
                "DSH",
                CheckState.Missing,
                $"没找到 DSH 数据目录：{config.Dsh.HomeDirectory}",
                "先安装并运行一次 DeepSeek Harness（它是 AI 能力的来源，不在本程序内）。"));
        }
        else if (!environment.DshPortOpen)
        {
            items.Add(new Item(
                "DSH",
                CheckState.Pending,
                $"数据目录在（{config.Dsh.HomeDirectory}），但 API 端口 {config.Dsh.Port} 没在监听",
                "启动 DSH。它需要处于运行状态，桥接才能调用模型。"));
        }
        else
        {
            items.Add(new Item("DSH", CheckState.Ready, $"运行中（端口 {config.Dsh.Port}）", null));
        }

        // ── 2. SnowLuma：负责连 QQ ──
        if (environment.SnowLumaDir is null)
        {
            items.Add(new Item(
                "SnowLuma（连 QQ）",
                CheckState.Missing,
                "没找到安装目录",
                "把 SnowLuma 放到程序目录下的 services\\snowluma，或在「设置」页指定它的位置。"));
        }
        else if (!environment.SnowLumaWebUiOpen)
        {
            items.Add(new Item(
                "SnowLuma（连 QQ）",
                CheckState.Pending,
                $"目录已找到（{environment.SnowLumaDir}），但 WebUI 端口 {config.SnowLuma.PreferredWebUiPort} 没在监听",
                "到「服务管理」页点一键启动，或确认它已经在运行。"));
        }
        else
        {
            items.Add(new Item("SnowLuma（连 QQ）", CheckState.Ready, $"运行中（端口 {config.SnowLuma.PreferredWebUiPort}）", null));
        }

        // ── 3. OneBot：QQ 与桥接之间的协议口 ──
        // 它由 SnowLuma 在「账号登录之后」才开启，所以没监听几乎总是"还没登录"。
        if (!environment.OneBotPortOpen)
        {
            items.Add(new Item(
                "OneBot 接口",
                CheckState.Pending,
                "端口 3000 / 3001 没在监听",
                // 这两个端口由 SnowLuma 在 QQ 侧就绪后开启，属于它的范畴。
                // 这里只报事实 —— 怎么让 QQ 那边就绪不是本程序该管的事。
                "OneBot 端口由 SnowLuma 在 QQ 侧就绪后开启。"));
        }
        else
        {
            items.Add(new Item("OneBot 接口", CheckState.Ready, "3000 / 3001 监听中", null));
        }

        // ── 4. qq-bridge：把 QQ 消息接给 AI ──
        if (environment.BridgeDir is null)
        {
            items.Add(new Item(
                "qq-bridge（接 AI）",
                CheckState.Missing,
                "没找到项目目录",
                "把 qq-bridge 放到程序目录下的 services\\qq-bridge，或在「设置」页指定它的位置。"));
        }
        else if (!environment.BridgeConsoleOpen)
        {
            items.Add(new Item(
                "qq-bridge（接 AI）",
                CheckState.Pending,
                $"目录已找到（{environment.BridgeDir}），但控制台端口 {config.Bridge.ConsolePort} 没在监听",
                "到「服务管理」页点一键启动。"));
        }
        else
        {
            items.Add(new Item("qq-bridge（接 AI）", CheckState.Ready, $"运行中（端口 {config.Bridge.ConsolePort}）", null));
        }

        // ── 5. 管理员 QQ：审批与管理员命令要靠它识别身份 ──
        if (bridgeStatus is null)
        {
            items.Add(new Item(
                "管理员 QQ 号",
                CheckState.Pending,
                "桥接还没连上，读不到",
                "先让 qq-bridge 跑起来（「服务管理」页一键启动），再回来核对这一项。"));
        }
        else if (bridgeStatus.OwnerQQ is null or <= 0)
        {
            items.Add(new Item(
                "管理员 QQ 号",
                CheckState.Missing,
                "还没填",
                "到「桥接控制台 → 白名单」页填上你自己的 QQ 号 —— "
                + "审批只有管理员本人的消息才生效，不填就用不了。"));
        }
        else
        {
            items.Add(new Item("管理员 QQ 号", CheckState.Ready, bridgeStatus.OwnerQQ.Value.ToString(), null));
        }

        // ── 6. 白名单：决定她在哪些群/私聊里工作 ──
        var groupCount = bridgeStatus?.AllowGroups.Count ?? 0;
        var privateCount = bridgeStatus?.AllowPrivate.Count ?? 0;

        if (bridgeStatus is null)
        {
            items.Add(new Item(
                "启用范围",
                CheckState.Pending,
                "桥接还没连上，读不到",
                null));
        }
        else if (groupCount == 0 && privateCount == 0)
        {
            items.Add(new Item(
                "启用范围",
                CheckState.Missing,
                "白名单是空的 —— 她不会在任何会话里回应",
                "到「桥接控制台 → 白名单」页勾选要启用的群。"));
        }
        else
        {
            items.Add(new Item(
                "启用范围",
                CheckState.Ready,
                $"{groupCount} 个群 / {privateCount} 个私聊",
                null));
        }

        var ready = items.All(i => i.State == CheckState.Ready);
        return new Report(items, ready);
    }
}
