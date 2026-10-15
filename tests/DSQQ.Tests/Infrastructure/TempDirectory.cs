using System.IO;

namespace DSQQ.Tests.Infrastructure;

// 一次性临时目录，供测试隔离文件读写。
// 为什么需要它：AppConfigStore 的默认路径指向真实用户配置
// （%APPDATA%\DSQQ\config.json）。测试必须改写临时路径，
// 绝不能因为跑一次单测而覆盖开发者本人的配置。
public sealed class TempDirectory : IDisposable
{
    // 创建一个位于系统临时目录下的唯一目录。
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dsqq-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    // 临时目录的绝对路径。
    public string Path { get; }

    // 在临时目录内拼出一个文件路径。
    // 参数 relativePath：相对路径，例如 config.json。
    // 返回：绝对路径。
    public string File(string relativePath) => System.IO.Path.Combine(Path, relativePath);

    // 尽力删除临时目录；失败时静默忽略，不影响测试结论。
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
