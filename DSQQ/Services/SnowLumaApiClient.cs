using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSQQ.Models;

namespace DSQQ.Services;

/// <summary>登录结果类型。</summary>
public enum LoginOutcome
{
    /// <summary>登录成功。</summary>
    Success,

    /// <summary>密码错误。</summary>
    BadPassword,

    /// <summary>需要两步验证码。</summary>
    NeedsTotp,

    /// <summary>尝试过于频繁（被限流）。</summary>
    RateLimited,

    /// <summary>网络或服务不可达。</summary>
    Unreachable,
}

/// <summary>登录结果。</summary>
/// <param name="Outcome">结果类型。</param>
/// <param name="Message">面向用户的消息。</param>
public readonly record struct LoginResult(LoginOutcome Outcome, string Message);

/// <summary>
/// SnowLuma WebUI（默认 127.0.0.1:5099）的 HTTP 客户端。
/// </summary>
/// <remarks>
/// 鉴权方式：<c>POST /api/login</c> 返回一次性 token，同时写入会话 Cookie；
/// 后续请求靠 Cookie 维持。这里用 <see cref="System.Net.CookieContainer"/> 自动处理，
/// 并把 Cookie 持久化到本地（DPAPI 加密），实现"登录一次后免登录"。
/// </remarks>
public sealed class SnowLumaApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;

    /// <summary>初始化客户端。</summary>
    /// <param name="baseUri">WebUI 基地址，例如 <c>http://127.0.0.1:5099</c>。</param>
    /// <param name="timeout">请求超时。</param>
    public SnowLumaApiClient(Uri baseUri, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        BaseUri = baseUri;

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DSQQ/0.1");
    }

    /// <summary>WebUI 基地址。</summary>
    public Uri BaseUri { get; }

    /// <summary>当前是否已登录（依据会话 Cookie 是否存在）。</summary>
    public bool HasSession => _cookies.GetCookies(BaseUri).Count > 0;

    /// <summary>登录。</summary>
    /// <param name="password">WebUI 密码。</param>
    /// <param name="totp">两步验证码；未启用两步验证时留空。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>登录结果。</returns>
    public async Task<LoginResult> LoginAsync(
        string password,
        string? totp = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            return new LoginResult(LoginOutcome.BadPassword, "密码为空。");
        }

        try
        {
            var payload = new Dictionary<string, object> { ["password"] = password };
            if (!string.IsNullOrWhiteSpace(totp))
            {
                payload["totp"] = totp;
            }

            using var response = await _http
                .PostAsync("/api/login", new StringContent(
                    JsonSerializer.Serialize(payload),
                    System.Text.Encoding.UTF8,
                    "application/json"), cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = SafeDeserialize<LoginResponse>(body);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return new LoginResult(
                    LoginOutcome.RateLimited,
                    result?.Message ?? "登录尝试过多，请稍后再试。");
            }

            if (result?.Success == true)
            {
                return new LoginResult(LoginOutcome.Success, "登录成功。");
            }

            if (result?.NeedsTotp == true)
            {
                return new LoginResult(LoginOutcome.NeedsTotp, "该账号启用了两步验证，请输入验证码。");
            }

            return new LoginResult(
                LoginOutcome.BadPassword,
                result?.Message ?? $"登录失败（HTTP {(int)response.StatusCode}）。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new LoginResult(LoginOutcome.Unreachable, $"无法连接 SnowLuma WebUI：{ex.GetType().Name}");
        }
    }

    /// <summary>登出（同时清除本地会话）。</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsync("/api/logout", null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // 登出失败也继续清本地会话。
        }

        _cookies.GetCookies(BaseUri).ToList().ForEach(c => c.Expired = true);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>读取运行状态。</summary>
    public Task<SnowLumaStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetAsync<SnowLumaStatus>("/api/status", cancellationToken);

    /// <summary>读取 QQ 进程与注入状态。</summary>
    public async Task<IReadOnlyList<SnowLumaProcess>> GetProcessesAsync(CancellationToken cancellationToken = default)
    {
        var list = await GetAsync<List<SnowLumaProcess>>("/api/processes", cancellationToken).ConfigureAwait(false);
        return list ?? [];
    }

    /// <summary>读取指定账号的 OneBot 配置。</summary>
    public Task<SnowLumaOneBotConfig?> GetOneBotConfigAsync(string uin, CancellationToken cancellationToken = default)
        => GetAsync<SnowLumaOneBotConfig>($"/api/config/{uin}", cancellationToken);

    /// <summary>
    /// 导出会话 Cookie，供持久化使用。
    /// </summary>
    /// <returns>形如 <c>name=value; name2=value2</c> 的字符串；无会话时为空串。</returns>
    public string ExportCookies()
    {
        var cookies = _cookies.GetCookies(BaseUri);
        return cookies.Count == 0
            ? string.Empty
            : string.Join("; ", cookies.Cast<System.Net.Cookie>().Select(c => $"{c.Name}={c.Value}"));
    }

    /// <summary>导入之前持久化的会话 Cookie。</summary>
    /// <param name="cookieHeader">由 <see cref="ExportCookies"/> 生成的字符串。</param>
    public void ImportCookies(string cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return;
        }

        foreach (var pair in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            try
            {
                _cookies.Add(BaseUri, new System.Net.Cookie(pair[..index], pair[(index + 1)..], "/", BaseUri.Host));
            }
            catch (System.Net.CookieException)
            {
                // 单个 Cookie 非法时跳过，不影响其余。
            }
        }
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return SafeDeserialize<T>(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    private static T? SafeDeserialize<T>(string json)
        where T : class
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <summary>登录接口的响应体。</summary>
    private sealed class LoginResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("needsTotp")]
        public bool NeedsTotp { get; set; }
    }
}
