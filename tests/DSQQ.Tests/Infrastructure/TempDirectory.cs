using System.IO;

namespace DSQQ.Tests.Infrastructure;

/// <summary>
/// 一次性临时目录，供测试隔离文件读写。
/// </summary>
/// <remarks>
/// 为什么需要它：<c>AppConfigStore</c> 的默认路径指向真实用户配置
/// （<c>%APPDATA%\DSQQ\config.json</c>）。测试必须改写临时路径，
/// 绝不能因为跑一次单测而覆盖开发者本人的配置。
/// </remarks>
public sealed class TempDirectory : IDisposable
{
    /// <summary>创建一个位于系统临时目录下的唯一目录。</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dsqq-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    /// <summary>临时目录的绝对路径。</summary>
    public string Path { get; }

    /// <summary>在临时目录内拼出一个文件路径。</summary>
    /// <param name="relativePath">相对路径，例如 <c>config.json</c>。</param>
    /// <returns>绝对路径。</returns>
    public string File(string relativePath) => System.IO.Path.Combine(Path, relativePath);

    /// <summary>尽力删除临时目录；失败时静默忽略，不影响测试结论。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录残留无害；测试不应因为清理失败而失败。
        }
    }
}
