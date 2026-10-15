using System.IO;
using System.Text.Json;
using DSQQ.Models;

namespace DSQQ.Services;

/// <summary>
/// 应用配置的读写。
/// </summary>
/// <remarks>
/// 健壮性约定（对应"注意代码健壮性"的要求）：
/// <list type="bullet">
/// <item><see cref="TryLoad"/> **永不抛异常**：文件缺失、JSON 损坏、字段类型不符都只是返回 <c>null</c>
/// 并通过 <c>error</c> 说明原因，界面据此提示并退化到默认配置；</item>
/// <item><see cref="Save"/> 采用"先写临时文件再替换"的方式，避免写一半断电/崩溃导致配置被截断；</item>
/// <item>所有路径都可通过参数注入，便于单元测试使用临时目录，不污染真实用户配置。</item>
/// </list>
/// </remarks>
public static class AppConfigStore
{
    private const string FileName = "config.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        // 中文（如默认告警关键字）不转义为 \uXXXX，便于用户直接阅读与编辑配置。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 配置目录：<c>%APPDATA%\DSQQ</c>（随用户漫游，不写入程序目录）。
    /// </summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DSQQ");

    /// <summary>
    /// 默认配置文件路径：<c>%APPDATA%\DSQQ\config.json</c>。
    /// </summary>
    public static string DefaultPath { get; } = Path.Combine(DefaultDirectory, FileName);

    /// <summary>
    /// 尝试读取配置。
    /// </summary>
    /// <param name="path">配置文件路径；为 <c>null</c> 时使用 <see cref="DefaultPath"/>。</param>
    /// <param name="error">读取失败原因；成功时为 <c>null</c>。</param>
    /// <returns>读取到的配置；文件不存在或内容不可用时为 <c>null</c>。</returns>
    public static AppConfig? TryLoad(string? path, out string? error)
    {
        var target = path ?? DefaultPath;
        error = null;

        try
        {
            if (!File.Exists(target))
            {
                error = "配置文件尚未创建（将使用默认值，保存后生成）";
                return null;
            }

            var json = File.ReadAllText(target);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "配置文件为空";
                return null;
            }

            var config = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            if (config is null)
            {
                error = "配置文件内容无法解析为配置对象";
                return null;
            }

            return config;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 读配置失败是可以恢复的：调用方会退化到默认值，因此这里不抛出。
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// 使用默认路径读取配置。
    /// </summary>
    /// <param name="error">读取失败原因；成功时为 <c>null</c>。</param>
    /// <returns>读取到的配置；不可用时为 <c>null</c>。</returns>
    public static AppConfig? TryLoad(out string? error) => TryLoad(null, out error);

    /// <summary>
    /// 保存配置（原子写：临时文件 + 替换）。
    /// </summary>
    /// <param name="config">要保存的配置。</param>
    /// <param name="path">目标路径；为 <c>null</c> 时使用 <see cref="DefaultPath"/>。</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 <c>null</c>。</exception>
    /// <exception cref="IOException">目录或文件不可写。</exception>
    public static void Save(AppConfig config, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var target = path ?? DefaultPath;
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, SerializerOptions);
        var temporary = target + ".tmp";

        // 先写临时文件：即使中途失败，原配置文件仍保持完整可用。
        File.WriteAllText(temporary, json);

        if (File.Exists(target))
        {
            File.Replace(temporary, target, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporary, target);
        }
    }
}
