using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DSQQ.Services;

/// <summary>
/// 本地敏感数据的加密存储。
/// </summary>
/// <remarks>
/// 使用 Windows DPAPI（<see cref="ProtectedData"/>，CurrentUser 范围）：
/// 密文只能被当前用户在本机解密，因此即便配置文件被复制到别处也无法还原明文。
/// 选择"按值加密后存进配置文件"，而不是另建密码库，
/// 是为了让用户在需要时能一眼看出"这里存了一个加密后的凭据"。
/// </remarks>
public static class SecureStore
{
    private const string Prefix = "dpapi:";

    /// <summary>加密文本。</summary>
    /// <param name="plainText">明文；为空时返回空串。</param>
    /// <returns>带 <c>dpapi:</c> 前缀的 Base64 密文。</returns>
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(encrypted);
        }
        catch (CryptographicException)
        {
            // 加密不可用（极少见）时返回空串，绝不退化为明文存储。
            return string.Empty;
        }
    }

    /// <summary>解密文本。</summary>
    /// <param name="protectedText">由 <see cref="Protect"/> 生成的字符串。</param>
    /// <returns>明文；无法解密时返回 <c>null</c>。</returns>
    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return null;
        }

        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // 兼容早期可能写入的明文（仅用于平滑升级，不鼓励）。
            return protectedText;
        }

        try
        {
            var raw = Convert.FromBase64String(protectedText[Prefix.Length..]);
            var decrypted = ProtectedData.Unprotect(raw, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // 换机器/换用户后无法解密，属预期情况：调用方会要求重新输入。
            return null;
        }
    }

    /// <summary>把敏感值写入文件（加密后）。</summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="plainText">明文。</param>
    public static void WriteProtectedFile(string path, string plainText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Protect(plainText), Encoding.UTF8);
    }

    /// <summary>从文件读取敏感值（解密）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>明文；文件不存在或无法解密时返回 <c>null</c>。</returns>
    public static string? ReadProtectedFile(string path)
    {
        try
        {
            return File.Exists(path) ? Unprotect(File.ReadAllText(path, Encoding.UTF8)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
