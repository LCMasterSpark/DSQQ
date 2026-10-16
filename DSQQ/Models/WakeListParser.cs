namespace DSQQ.Models;

// 唤醒配置里那两个"一串东西"的字段，切分规则**不一样**，所以各自命名、放在一起。
//
// 差异来自桥接服务端（`D:\qq-bridge\src\bridge.js`）：
//   keywords   只做 `map(trim)` —— 它把数组元素当成完整关键词，含空格也照收
//              （默认关键词里就有「DeepSeek V3」「DeepSeek R1」）。
//   speakerIds 走 `normalizeSpeakerIdsV2`（`bridge.js:8621`），按 `[,，\s]+` 切分，
//              因为 QQ 号之间用空格分隔是常见写法。
//
// 这里曾经两个都用「逗号或空白」切。后果是每保存一次就把「DeepSeek V3」拆成
// 「DeepSeek」和「V3」：界面看起来一切正常，配置却被悄悄改写，而且越保存越碎。
// 单元测试用自造样本测不出来 —— 只有拿真实配置往返一次才会暴露。
public static class WakeListParser
{
    // 关键词：只按逗号（半角/全角）和换行切，**空格是内容的一部分**。
    public static List<string> ParseKeywords(string? text)
        => Split(text, [',', '，', '\r', '\n']);

    // 指定成员：与桥接的 normalizeSpeakerIdsV2 保持一致，空白也算分隔符。
    public static List<string> ParseSpeakerIds(string? text)
        => Split(text, [',', '，', '\r', '\n', ' ', '\t']);

    private static List<string> Split(string? text, char[] separators)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
