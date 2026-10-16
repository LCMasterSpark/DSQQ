namespace DSQQ.Models;

// 把桥接报的 SnowLuma 上游状态写成一句人话。
//
// 为什么要专门做这个：总览页原先写的是 `SnowLuma 连接 {snowLuma?.Connected}`，而桥接侧的
// `connected` 在「第一次连上之前」是 undefined —— `JSON.stringify` 会**把整个键丢掉**，
// 于是故障时那一格渲染成空白，旁边「接收健康」还是 True，看板上毫无异常，
// 可实际上一个包都没收到（2026-10-16 真实故障）。
//
// 空白是最坏的一种显示：读的人无法区分「正常」「不知道」「桥接没返回这一项」。
// 所以这里宁可啰嗦，也不留空白 —— 每个分支都必须给出一句能读的话。
public static class SnowLumaHealthText
{
    // 只有「连着 且 收得到包」才算健康。
    //
    // 没连上**必须**判成不健康。总览页原先写的是 `connected ? Degraded : Online` ——
    // 没连上反而算正常，于是最严重的那种故障（握手卡死、一个包都收不到）
    // 在看板上是绿的，只能靠"机器人不说话"来发现。
    public static bool IsHealthy(BridgeSnowLumaStatus? snowLuma)
        => snowLuma is { Connected: true, Good: true };

    public static string Describe(BridgeSnowLumaStatus? snowLuma)
    {
        if (snowLuma is null)
        {
            return "状态未知（桥接没返回这一项）";
        }

        if (!snowLuma.Connected)
        {
            return snowLuma.DisconnectedSec is { } seconds
                ? $"没连上（已经 {FormatSpan(seconds)}）"
                : "没连上";
        }

        if (snowLuma.LastPacketAgoMs is not { } agoMs)
        {
            return "已连接，但一个包都还没收到";
        }

        var kind = string.IsNullOrWhiteSpace(snowLuma.LastPacketKind) ? "未知类型" : snowLuma.LastPacketKind;
        return $"已连接（{FormatSpan(agoMs / 1000)}前收到过 {kind}）";
    }

    private static string FormatSpan(long seconds) => seconds switch
    {
        < 0 => "0 秒",
        < 60 => $"{seconds} 秒",
        < 3600 => $"{seconds / 60} 分钟",
        _ => $"{seconds / 3600} 小时 {seconds % 3600 / 60} 分钟",
    };
}
