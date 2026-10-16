using System.Text.Json.Serialization;

namespace DSQQ.Models;

// 桥接 /api/voice/list 的响应。
// 控制台通道不带 agent token 时不需要 key，并且会带回 dir（AI 通道下 dir 会被抑制，防止路径外泄）。
public sealed class VoiceListResponse
{
    // 是否成功。
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    // 失败原因。
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    // 语音库目录的绝对路径（只有控制台通道能看到）。
    [JsonPropertyName("dir")]
    public string? Dir { get; set; }

    // 本次列出的条数。
    [JsonPropertyName("count")]
    public int Count { get; set; }

    // 音频文件列表。
    [JsonPropertyName("voices")]
    public List<VoiceEntry> Voices { get; set; } = [];

    // 桥接给的提示语。库为空时它会直接告诉你该把文件放进哪个目录。
    [JsonPropertyName("hint")]
    public string? Hint { get; set; }

    // 上限与限流。
    [JsonPropertyName("limits")]
    public VoiceLimits? Limits { get; set; }
}

// 语音库里的一个音频文件。
public sealed class VoiceEntry
{
    // 相对语音库目录的路径（子目录用 / 分隔）。
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    // 桥接探测的结果：这个文件能不能当语音发出去。
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    // 字节数。
    [JsonPropertyName("bytes")]
    public long? Bytes { get; set; }

    // 桥接拼好的人话大小，例如「31.3 KB」。
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    // 时长（秒）。ffprobe 不可用时为 null —— 那种情况下桥接不做时长校验。
    [JsonPropertyName("seconds")]
    public double? Seconds { get; set; }

    // 探测失败的原因。
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    // 列表里显示的整行文字。
    public string Display
    {
        get
        {
            var mark = Ok ? "✓" : "✗";
            var parts = new List<string> { $"{mark} {Name}" };

            if (!string.IsNullOrWhiteSpace(Detail))
            {
                parts.Add(Detail);
            }

            if (Seconds is > 0)
            {
                parts.Add($"{Seconds.Value:0.#} 秒");
            }

            var line = string.Join("　", parts);

            if (!Ok && !string.IsNullOrWhiteSpace(Error))
            {
                line += $"　（{Error}）";
            }

            return line;
        }
    }
}

// 语音的上限与限流。
public sealed class VoiceLimits
{
    // 单条最长秒数。
    [JsonPropertyName("maxSeconds")]
    public double? MaxSeconds { get; set; }

    // 单条最大字节数。
    [JsonPropertyName("maxBytes")]
    public long? MaxBytes { get; set; }

    // 每分钟最多几条。
    [JsonPropertyName("maxPerMinute")]
    public int? MaxPerMinute { get; set; }

    // 每小时最多几条。
    [JsonPropertyName("maxPerHour")]
    public int? MaxPerHour { get; set; }

    // 一句话说清这套限制。
    public string Display
    {
        get
        {
            var parts = new List<string>();

            if (MaxSeconds is > 0)
            {
                parts.Add($"单条最长 {MaxSeconds.Value / 60:0.#} 分钟");
            }

            if (MaxBytes is > 0)
            {
                parts.Add($"最大 {FormatBytes(MaxBytes.Value)}");
            }

            if (MaxPerMinute is > 0)
            {
                parts.Add($"每分钟最多 {MaxPerMinute} 条");
            }

            if (MaxPerHour is > 0)
            {
                parts.Add($"每小时最多 {MaxPerHour} 条");
            }

            return parts.Count == 0 ? "（桥接没有给出限制）" : string.Join("　·　", parts);
        }
    }

    // 把字节数写成人话。语音上限动辄 20 MB，写成数字没人算得过来。
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024 / 1024:0.##} GB";
        }

        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024:0.#} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes} 字节";
    }
}

// 桥接 /api/send/voice 的响应（试发与真发共用一套字段）。
public sealed class VoiceSendResponse
{
    // 是否成功。
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    // 失败原因。
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    // 这次只是试算，没有真的发出去。
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    // 目标会话。
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    // 解析到的音频绝对路径。
    [JsonPropertyName("file")]
    public string? File { get; set; }

    // 桥接是怎么找到这个文件的（例如 voice-dir 表示在语音库目录里找到）。
    [JsonPropertyName("via")]
    public string? Via { get; set; }

    // 字节数。
    [JsonPropertyName("bytes")]
    public long? Bytes { get; set; }

    // 时长（秒）。ffprobe 不可用时为 null。
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    // 编码格式。
    [JsonPropertyName("codec")]
    public string? Codec { get; set; }

    // 桥接拼好的人话描述。
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    // 警告。**必须显示** —— 例如「无法探测时长（ffprobe 不可用）：将不做时长校验」，
    // 这是"降级了但没失败"，不显示的话用户会以为时长真的被检查过了。
    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    // 上限与限流。
    [JsonPropertyName("limits")]
    public VoiceLimits? Limits { get; set; }

    // 发出去之后的消息 ID。
    [JsonPropertyName("messageId")]
    public object? MessageId { get; set; }

    // 桥接是否落地校验成功（真的在会话里看到了这条消息）。
    [JsonPropertyName("verified")]
    public bool? Verified { get; set; }

    // 语音转写回读的文字（只在明确要求回读时才有）。
    [JsonPropertyName("readback")]
    public string? Readback { get; set; }

    // 实际用了什么音频处理。
    [JsonPropertyName("audioProcessing")]
    public string? AudioProcessing { get; set; }

    // 实际用的音量 / 响度。
    [JsonPropertyName("volume")]
    public double? Volume { get; set; }

    [JsonPropertyName("loudness")]
    public double? Loudness { get; set; }

    // 结果区顶上的那一行。
    public string Summary
    {
        get
        {
            if (!Ok)
            {
                return $"✗ 失败　{Error ?? "（桥接没给原因）"}";
            }

            var head = DryRun ? "✓ 检查通过（没有真的发出去）" : "✓ 已发送";

            var parts = new List<string> { head };

            if (!string.IsNullOrWhiteSpace(Key))
            {
                parts.Add(Key!);
            }

            if (!string.IsNullOrWhiteSpace(Detail))
            {
                parts.Add(Detail!);
            }

            if (!DryRun)
            {
                if (Verified == true)
                {
                    parts.Add("已确认到达会话");
                }
                else if (Verified == false)
                {
                    parts.Add("⚠ 没能确认到达会话");
                }

                if (MessageId is not null && MessageId.ToString() != "0")
                {
                    parts.Add($"消息 ID {MessageId}");
                }
            }

            return string.Join("　·　", parts);
        }
    }

    // 结果区的正文：桥接原话 + 警告 + 音频处理细节。
    public string DetailText
    {
        get
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(File))
            {
                lines.Add($"文件：{File}" + (string.IsNullOrWhiteSpace(Via) ? string.Empty : $"（来源：{Via}）"));
            }

            if (!string.IsNullOrWhiteSpace(Codec))
            {
                lines.Add($"编码：{Codec}");
            }

            if (Duration is > 0)
            {
                lines.Add($"时长：{Duration.Value:0.#} 秒");
            }

            if (AudioProcessing is not null || Volume is not null || Loudness is not null)
            {
                var audio = new List<string>();

                if (!string.IsNullOrWhiteSpace(AudioProcessing))
                {
                    audio.Add(AudioProcessing!);
                }

                if (Volume is not null)
                {
                    audio.Add($"音量 {Volume:0.##}");
                }

                if (Loudness is not null)
                {
                    audio.Add($"响度 {Loudness:0.##}");
                }

                lines.Add($"音频处理：{string.Join("　", audio)}");
            }

            if (Readback is not null)
            {
                lines.Add($"转写回读：{Readback}");
            }

            if (Warnings.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("警告（不影响这次发送，但说明有东西被降级了）：");
                lines.AddRange(Warnings.Select(w => $"　· {w}"));
            }

            return lines.Count == 0 ? "（桥接没有给出细节）" : string.Join(Environment.NewLine, lines);
        }
    }
}
