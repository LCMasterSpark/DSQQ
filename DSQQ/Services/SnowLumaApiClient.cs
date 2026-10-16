using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
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
//
// 鉴权方式（以实测为准）：POST /api/login 返回 {success, token}，
// 之后所有请求带 `Authorization: Bearer <token>`。
//
// ⚠️ 不要改用 Cookie。登录响应里确实有一个 set-cookie
// （snowluma_avatar_session），但它的 Path 是 **/avatar** —— 那是给头像图片用的。
// 拿它去访问数据接口一律 401，表现是"登录成功、之后什么都读不到"。
// token 会被持久化到本地（DPAPI 加密），实现"登录一次后免登录"。
public sealed class SnowLumaApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    // 当前会话 token；未登录时为 null。
    private string? _token;

    // 最近一次请求失败的原因，给人看的。每次请求开始时清空。
    // 视图层靠它说明"为什么读不到"，而不是只留一个空白让人猜。
    public string? LastError { get; private set; }

    // 初始化客户端。
    // 参数 baseUri：WebUI 基地址，例如 http://127.0.0.1:5099。
    // 参数 timeout：请求超时。
    public SnowLumaApiClient(Uri baseUri, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        BaseUri = baseUri;

        _http = new HttpClient()
        {
            BaseAddress = baseUri,
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DSQQ/0.1");
    }

    // WebUI 基地址。
    public Uri BaseUri { get; }

    // 当前是否已登录（依据会话 token 是否存在）。
    public bool HasSession => !string.IsNullOrEmpty(_token);

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
                // 把 token 留下来 —— 后续所有 API 调用都要靠它。
                // 登录响应里那个 snowluma_avatar_session cookie 只对 /avatar 有效，
                // 拿它去访问数据接口一律 401（实测确认），所以这里不能靠 cookie。
                SetToken(result.Token);
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

        SetToken(null);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // 读取运行状态。
    public Task<SnowLumaStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetAsync<SnowLumaStatus>("/api/status", cancellationToken);

    // 读取 QQ 进程与注入状态。
    public async Task<IReadOnlyList<SnowLumaProcess>> GetProcessesAsync(CancellationToken cancellationToken = default)
    {
        // 顶层是 {"list":[…]}；按裸数组解析会抛 JsonException 被吞成 null → 列表恒为空。
        var wrapped = await GetAsync<SnowLumaProcessList>("/api/processes", cancellationToken).ConfigureAwait(false);
        return wrapped?.List ?? [];
    }

    // 读取指定账号的 OneBot 配置。
    //
    // ⚠️ 响应有 config 包装层（{"config":{…}}），必须解一层 ——
    // 直接按裸对象解析会让 Networks 恒为 null，界面误报"还没有任何接入点"。
    public async Task<SnowLumaOneBotConfig?> GetOneBotConfigAsync(
        string uin, CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<SnowLumaOneBotConfigResponse>($"/api/config/{uin}", cancellationToken)
            .ConfigureAwait(false);
        return response?.Config;
    }

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

    // ── 安全与通知 ──

    // 读取登录状态（是否被要求改密码）。
    public Task<SnowLumaAuthState?> GetAuthStateAsync(CancellationToken cancellationToken = default)
        => GetAsync<SnowLumaAuthState>("/api/auth/state", cancellationToken);

    // 读取两步验证状态。
    public Task<SnowLumaTotpState?> GetTotpStateAsync(CancellationToken cancellationToken = default)
        => GetAsync<SnowLumaTotpState>("/api/auth/totp", cancellationToken);

    // 检查密码强度。返回逐条规则的通过情况，界面可以实时给反馈 ——
    // 比"提交后被拒绝"友好得多。
    public Task<SnowLumaStrengthResult?> CheckPasswordStrengthAsync(
        string password, CancellationToken cancellationToken = default)
        => PostJsonAsync<SnowLumaStrengthResult>("/api/auth/check-strength", new { password }, cancellationToken);

    // 修改 WebUI 密码。字段名以服务端实现为准：oldPassword / newPassword。
    public Task<SnowLumaApiResult?> ChangePasswordAsync(
        string oldPassword, string newPassword, CancellationToken cancellationToken = default)
        => PostJsonAsync<SnowLumaApiResult>(
            "/api/auth/change-password", new { oldPassword, newPassword }, cancellationToken);

    // 开始启用两步验证。返回**原始 JSON 文本** ——
    // 响应里含一次性密钥/otpauth URL，字段名随版本可能变，猜错不如原样展示给用户。
    public async Task<string?> BeginTotpAsync(
        string? accountName = null, string? issuer = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http
                .PostAsync(
                    "/api/auth/totp/begin",
                    new StringContent(
                        JsonSerializer.Serialize(new { accountName, issuer }, JsonOptions),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                    cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    // 确认启用两步验证（需要动态码与当前密码）。
    public Task<SnowLumaApiResult?> ConfirmTotpAsync(
        string code, string password, CancellationToken cancellationToken = default)
        => PostJsonAsync<SnowLumaApiResult>("/api/auth/totp/confirm", new { code, password }, cancellationToken);

    // 关闭两步验证（需要当前密码与一个有效动态码）。
    public Task<SnowLumaApiResult?> DisableTotpAsync(
        string password, string totp, CancellationToken cancellationToken = default)
        => PostJsonAsync<SnowLumaApiResult>("/api/auth/totp/disable", new { password, totp }, cancellationToken);

    // 读取通知配置。
    public async Task<SnowLumaNotificationsConfig?> GetNotificationsConfigAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<SnowLumaNotificationsResponse>("/api/notifications/config", cancellationToken)
            .ConfigureAwait(false);
        return response?.Config;
    }

    // 保存通知配置。
    // ⚠️ 整体覆盖语义：调用方必须先读出来、只改自己要改的字段再整体提交 ——
    // channels 里是没建模的结构，回写时必须原样带上，否则渠道会被清空。
    public Task<SnowLumaApiResult?> SaveNotificationsConfigAsync(
        SnowLumaNotificationsConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return PostJsonAsync<SnowLumaApiResult>("/api/notifications/config", config, cancellationToken);
    }

    // 读取最近的通知记录（条目结构随渠道而变，返回原始 JSON 文本）。
    public async Task<IReadOnlyList<string>> GetRecentNotificationsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<SnowLumaRecentNotifications>("/api/notifications/recent", cancellationToken)
            .ConfigureAwait(false);

        return (response?.Recent ?? [])
            .Select(e => e.GetRawText())
            .ToList();
    }

    // 向指定渠道发一条测试通知。
    public Task<SnowLumaApiResult?> TestNotificationAsync(
        string channelId, CancellationToken cancellationToken = default)
        => PostJsonAsync<SnowLumaApiResult>("/api/notifications/test", new { channelId }, cancellationToken);

    // ── 调试面板（OneBot action 直调）──

    // 读取全部可调用的 action 及其参数声明。实测 198 个 action、约 180 KB。
    public Task<DebugActionList?> GetDebugActionsAsync(CancellationToken cancellationToken = default)
        => GetAsync<DebugActionList>("/api/debug/actions", cancellationToken);

    // 调用一个 action。
    // uin 必填：漏了服务端直接 400「无效的账号」，不是 404 也不是 500。
    // 成败看返回里的 status，不看 HTTP 状态码 —— 未知 action 同样是 HTTP 200。
    public Task<DebugInvokeResult?> InvokeDebugAsync(
        string uin, string action, JsonObject? parameters, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uin);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return PostJsonAsync<DebugInvokeResult>(
            "/api/debug/invoke",
            new { uin, action, @params = parameters ?? new JsonObject() },
            cancellationToken);
    }

    // 提交一个 JSON 请求体并把响应解析成 T。
    private async Task<T?> PostJsonAsync<T>(
        string path, object payload, CancellationToken cancellationToken)
        where T : class
    {
        LastError = null;

        try
        {
            using var response = await _http
                .PostAsync(
                    path,
                    new StringContent(
                        JsonSerializer.Serialize(payload, JsonOptions),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                    cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // 即使状态码不是 2xx 也尝试解析 —— 服务端把失败原因放在 message 里，
            // 丢掉它用户就只能看到"失败了"。
            var parsed = SafeDeserialize<T>(body);
            if (parsed is null)
            {
                // 连响应体都解析不出来时才轮到状态码说话
                LastError = string.IsNullOrWhiteSpace(body)
                    ? DescribeStatus(path, response.StatusCode) + "，响应体是空的。"
                    : $"{path} 的响应看不懂（结构可能和这一版对不上）。";
            }

            return parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            LastError = DescribeTransportError(path, ex);
            return null;
        }
    }

    // ── 系统与存储 ──

    // 读取系统信息（主机、CPU、内存、运行时长）。
    public Task<SnowLumaSystemInfo?> GetSystemInfoAsync(CancellationToken cancellationToken = default)
        => GetAsync<SnowLumaSystemInfo>("/api/system", cancellationToken);

    // 读取存储占用与日志保留设置。
    public Task<SnowLumaStorageInfo?> GetStorageAsync(CancellationToken cancellationToken = default)
        => GetAsync<SnowLumaStorageInfo>("/api/system/storage", cancellationToken);

    // 保存日志保留设置。
    // 注意端点只有 POST（GET /api/system/storage/settings 返回 404）——
    // 现有值要从 GetStorageAsync 的 settings.saved 里取。
    public async Task<bool> SaveStorageSettingsAsync(
        SnowLumaStorageSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            using var response = await _http
                .PostAsync(
                    "/api/system/storage/settings",
                    new StringContent(
                        JsonSerializer.Serialize(settings, JsonOptions),
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

    // 立即清理过期日志与临时文件。
    public Task<bool> CleanupStorageAsync(CancellationToken cancellationToken = default)
        => PostActionAsync("/api/system/storage/cleanup", cancellationToken);

    // 检查 SnowLuma 自身是否有新版本。
    public Task<SnowLumaUpdateInfo?> CheckUpdateAsync(CancellationToken cancellationToken = default)
        => GetAsync<SnowLumaUpdateInfo>("/api/update/check", cancellationToken);

    // 读取最近的日志。
    // 参数 limit：最多取多少条。
    public async Task<IReadOnlyList<SnowLumaLogEntry>> GetLogsAsync(
        int limit = 300, CancellationToken cancellationToken = default)
    {
        // 顶层是 {"list":[…]} —— 又一个外壳，按裸数组解析会得到空列表。
        var wrapped = await GetAsync<SnowLumaLogList>($"/api/logs?limit={limit}", cancellationToken)
            .ConfigureAwait(false);
        return wrapped?.List ?? [];
    }

    // 读取当前日志级别与可选项。
    public Task<SnowLumaLogLevels?> GetLogLevelAsync(CancellationToken cancellationToken = default)
        => GetAsync<SnowLumaLogLevels>("/api/logs/level", cancellationToken);

    // 设置日志级别。
    // 排查问题时调到 debug/trace 能看到更多细节，查完记得调回来 —— 低级别会显著增加日志量。
    public async Task<bool> SetLogLevelAsync(string level, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);

        try
        {
            using var response = await _http
                .PostAsync(
                    "/api/logs/level",
                    new StringContent(
                        JsonSerializer.Serialize(new { level }, JsonOptions),
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

    // 探测某个 QQ 进程登录的是哪个号。
    //
    // ⚠️ 这条接口**不需要注入** —— 它去问 QQ 自己的本地登录端口，
    // 所以「已加载」为假的进程也能问出 QQ 号。
    // ⚠️ 探不到时是 HTTP 200 + {"info":null}，返回值就是 null，不算失败。
    public async Task<SnowLumaProbeLoginInfo?> ProbeLoginAsync(
        int pid, CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<SnowLumaProbeLoginResponse>(
            $"/api/processes/{pid}/probe-login", cancellationToken).ConfigureAwait(false);
        return response?.Info;
    }

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

    // 导出会话 token，供持久化使用；未登录时为空串。
    public string ExportToken() => _token ?? string.Empty;

    // 导入之前持久化的会话 token。
    public void ImportToken(string? token) => SetToken(token);

    // 设置会话 token，并同步到 HttpClient 的默认请求头。
    //
    // 用 DefaultRequestHeaders 而不是每个方法各自加头：调用点有十几处，
    // 漏掉任何一处都会得到一个"静默 401"，而那种错误很难从界面上看出来。
    private void SetToken(string? token)
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token;

        _http.DefaultRequestHeaders.Authorization = _token is null
            ? null
            : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        LastError = null;

        try
        {
            using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastError = DescribeStatus(path, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = SafeDeserialize<T>(json);
            if (parsed is null && !string.IsNullOrWhiteSpace(json))
            {
                LastError = $"{path} 的响应看不懂（结构可能和这一版对不上）。";
            }

            return parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            LastError = DescribeTransportError(path, ex);
            return null;
        }
    }

    // 401 单列出来 —— 它几乎总是"会话过期"，值得直接说而不是让人对着状态码猜。
    private static string DescribeStatus(string path, HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => $"{path} 返回 401：会话已失效，重新登录一次。",
        HttpStatusCode.NotFound => $"{path} 返回 404：这一版 SnowLuma 可能没有这个端点。",
        _ => $"{path} 返回 HTTP {(int)status}",
    };

    private string DescribeTransportError(string path, Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException =>
            $"{path} 请求超时（{_http.Timeout.TotalSeconds:0} 秒内没有响应）。",
        HttpRequestException http => $"{path} 连不上 SnowLuma：{http.Message}",
        _ => $"{path} 请求失败：{ex.Message}",
    };

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

        // 登录成功后拿到的会话 token，后续请求靠它鉴权。
        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("needsTotp")]
        public bool NeedsTotp { get; set; }
    }
}
