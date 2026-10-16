using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSQQ.Models;

namespace DSQQ.Services;

// 登录结果类型。
public enum LoginOutcome
{
    // 登录成功。
    Success,

    // 密码错误。
    BadPassword,

    // 需要两步验证码。
    NeedsTotp,

    // 尝试过于频繁（被限流）。
    RateLimited,

    // 网络或服务不可达。
    Unreachable,
}

// 登录结果。
// 参数 Outcome：结果类型。
// 参数 Message：面向用户的消息。
public readonly record struct LoginResult(LoginOutcome Outcome, string Message);

// SnowLuma WebUI（默认 127.0.0.1:5099）的 HTTP 客户端。
// 鉴权方式：POST /api/login 返回一次性 token，同时写入会话 Cookie；
// 后续请求靠 Cookie 维持。这里用 System.Net.CookieContainer 自动处理，
// 并把 Cookie 持久化到本地（DPAPI 加密），实现"登录一次后免登录"。
public sealed class SnowLumaApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;

    // 初始化客户端。
    // 参数 baseUri：WebUI 基地址，例如 http://127.0.0.1:5099。
    // 参数 timeout：请求超时。
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

    // WebUI 基地址。
    public Uri BaseUri { get; }

    // 当前是否已登录（依据会话 Cookie 是否存在）。
    public bool HasSession => _cookies.GetCookies(BaseUri).Count > 0;

    // 登录。
    // 参数 password：WebUI 密码。
    // 参数 totp：两步验证码；未启用两步验证时留空。
    // 参数 cancellationToken：取消标记。
    // 返回：登录结果。
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

    // 登出（同时清除本地会话）。
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

    // 读取运行状态。
    public Task<SnowLumaStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetAsync<SnowLumaStatus>("/api/status", cancellationToken);

    // 读取 QQ 进程与注入状态。
    public async Task<IReadOnlyList<SnowLumaProcess>> GetProcessesAsync(CancellationToken cancellationToken = default)
    {
        var list = await GetAsync<List<SnowLumaProcess>>("/api/processes", cancellationToken).ConfigureAwait(false);
        return list ?? [];
    }

    // 读取指定账号的 OneBot 配置。
    public Task<SnowLumaOneBotConfig?> GetOneBotConfigAsync(string uin, CancellationToken cancellationToken = default)
        => GetAsync<SnowLumaOneBotConfig>($"/api/config/{uin}", cancellationToken);

    // 保存指定账号的 OneBot 配置。
    //
    // ⚠️ 这是**整体覆盖**语义：调用方必须先把现有配置读出来、改完再整体提交，
    // 否则没提交的字段会被清掉。模型里对未知字段用了 JsonExtensionData 兜底。
    public async Task<bool> SaveOneBotConfigAsync(
        string uin,
        SnowLumaOneBotConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uin);
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            using var response = await _http
                .PostAsync(
                    $"/api/config/{uin}",
                    new StringContent(
                        JsonSerializer.Serialize(config, JsonOptions),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                    cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    // 读取可用的 QQ 账号（登录成功后才有）。
    public async Task<IReadOnlyList<SnowLumaAccount>> GetQqListAsync(CancellationToken cancellationToken = default)
    {
        var list = await GetAsync<SnowLumaQqList>("/api/qq-list", cancellationToken).ConfigureAwait(false);
        return list?.List ?? [];
    }

    // 读取各账号的接入点状态（哪个端口在听、有几个客户端连上来）。
    public async Task<IReadOnlyList<SnowLumaConnection>> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var list = await GetAsync<SnowLumaConnections>("/api/connections", cancellationToken).ConfigureAwait(false);
        return list?.List ?? [];
    }

    // 把某个 QQ 进程加载（注入原生模块）进来。
    public Task<bool> LoadProcessAsync(int pid, CancellationToken cancellationToken = default)
        => PostActionAsync($"/api/processes/{pid}/load", cancellationToken);

    // 把某个 QQ 进程卸载出去。
    public Task<bool> UnloadProcessAsync(int pid, CancellationToken cancellationToken = default)
        => PostActionAsync($"/api/processes/{pid}/unload", cancellationToken);

    // 重新探测某个 QQ 进程的状态。
    public Task<bool> RefreshProcessAsync(int pid, CancellationToken cancellationToken = default)
        => PostActionAsync($"/api/processes/{pid}/refresh", cancellationToken);

    // 执行一个无请求体的 POST 动作，只关心是否成功。
    private async Task<bool> PostActionAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.PostAsync(path, null, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    // 导出会话 Cookie，供持久化使用。
    // 返回：形如 name=value; name2=value2 的字符串；无会话时为空串。
    public string ExportCookies()
    {
        var cookies = _cookies.GetCookies(BaseUri);
        return cookies.Count == 0
            ? string.Empty
            : string.Join("; ", cookies.Cast<System.Net.Cookie>().Select(c => $"{c.Name}={c.Value}"));
    }

    // 导入之前持久化的会话 Cookie。
    // 参数 cookieHeader：由 ExportCookies 生成的字符串。
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

    public void Dispose() => _http.Dispose();

    // 登录接口的响应体。
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
