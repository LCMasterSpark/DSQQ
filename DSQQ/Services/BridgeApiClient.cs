using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSQQ.Models;

namespace DSQQ.Services;

/// <summary>
/// qq-bridge 控制台（默认 127.0.0.1:3100）的 HTTP 客户端。
/// </summary>
/// <remarks>
/// 桥接自带控制台，其 HTTP API 覆盖了 Web 面板的全部功能。
/// 本程序的原生界面就建立在这套 API 上，不内嵌网页。
/// 注意：控制台鉴权是否开启由桥接配置决定；本程序依赖"已关闭鉴权或本机可信"的前提。
/// </remarks>
public sealed class BridgeApiClient : IDisposable
{
    /// <summary>反序列化选项：忽略大小写，容忍后端增删字段。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>初始化客户端。</summary>
    /// <param name="baseUri">控制台基地址，例如 <c>http://127.0.0.1:3100</c>。</param>
    /// <param name="timeout">请求超时。</param>
    public BridgeApiClient(Uri baseUri, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        BaseUri = baseUri;
        _http = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DSQQ/0.1");
    }

    /// <summary>控制台基地址。</summary>
    public Uri BaseUri { get; }

    /// <summary>读取运行状态。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>状态对象；请求失败或响应不可解析时返回 <c>null</c>。</returns>
    public Task<BridgeStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetAsync<BridgeStatus>("/api/status", cancellationToken);

    /// <summary>读取白名单与黑名单。</summary>
    public Task<BridgeWhitelist?> GetWhitelistAsync(CancellationToken cancellationToken = default)
        => GetAsync<BridgeWhitelist>("/api/whitelist", cancellationToken);

    /// <summary>
    /// 写入白名单。
    /// </summary>
    /// <param name="allowPrivate">允许的私聊号码。</param>
    /// <param name="allowGroups">允许的群号。</param>
    /// <param name="ownerQQ">管理员 QQ 号；为 <c>null</c> 表示不改动。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>是否写入成功。</returns>
    public Task<bool> SaveWhitelistAsync(
        IReadOnlyList<string> allowPrivate,
        IReadOnlyList<string> allowGroups,
        long? ownerQQ = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allowPrivate);
        ArgumentNullException.ThrowIfNull(allowGroups);

        var payload = new Dictionary<string, object?>
        {
            ["allow"] = new Dictionary<string, object?>
            {
                ["private"] = allowPrivate,
                ["groups"] = allowGroups,
            },
        };

        if (ownerQQ is not null)
        {
            payload["ownerQQ"] = ownerQQ;
        }

        return PostAsync("/api/whitelist", payload, cancellationToken);
    }

    /// <summary>读取机器人所在的所有群（带群名）。</summary>
    public async Task<IReadOnlyList<QqGroup>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<QqGroupList>("/api/qq/groups", cancellationToken).ConfigureAwait(false);
        return response?.Groups ?? [];
    }

    /// <summary>读取会话映射。</summary>
    public async Task<IReadOnlyList<BridgeSessionEntry>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<BridgeSessions>("/api/sessions", cancellationToken).ConfigureAwait(false);
        return response?.Sessions ?? [];
    }

    /// <summary>读取 token 与花费汇总。</summary>
    public Task<TokenSummary?> GetTokenSummaryAsync(CancellationToken cancellationToken = default)
        => GetAsync<TokenSummary>("/api/tokens/summary", cancellationToken);

    /// <summary>读取人设配置。</summary>
    public Task<PersonaResponse?> GetPersonaAsync(CancellationToken cancellationToken = default)
        => GetAsync<PersonaResponse>("/api/config/persona", cancellationToken);

    /// <summary>保存人设配置。</summary>
    /// <param name="persona">人设内容。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>是否保存成功（包含改写规则意图的内容会被后端拒绝）。</returns>
    public Task<bool> SavePersonaAsync(PersonaConfig persona, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        return PostAsync("/api/config/persona", persona, cancellationToken);
    }

    /// <summary>读取每日配额配置。</summary>
    public Task<QuotaConfig?> GetQuotaAsync(CancellationToken cancellationToken = default)
        => GetAsync<QuotaConfig>("/api/config/quota", cancellationToken);

    /// <summary>保存每日配额配置。</summary>
    public Task<bool> SaveQuotaAsync(QuotaConfig quota, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quota);
        return PostAsync("/api/config/quota", quota, cancellationToken);
    }

    /// <summary>读取模型与推理强度设置。</summary>
    public Task<ModelSelection?> GetModelSelectionAsync(CancellationToken cancellationToken = default)
        => GetAsync<ModelSelection>("/api/dsh/model-selection", cancellationToken);

    /// <summary>切换运行模式。</summary>
    /// <param name="mode">目标模式（chat / closed-agent / reserved / reserved2）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>是否切换成功。</returns>
    public Task<bool> SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        return PostAsync("/api/mode", new { mode }, cancellationToken);
    }

    /// <summary>
    /// 设置 DSH 思考强度。
    /// </summary>
    /// <param name="reasoningEffort">强度档位（low / high / max）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>是否设置成功。</returns>
    /// <remarks>该设置会同时写入 DSH 全局默认，对已有会话于下一条消息生效（无需重启）。</remarks>
    public Task<bool> SetReasoningEffortAsync(string reasoningEffort, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasoningEffort);
        return PostAsync("/api/dsh/effort", new { reasoningEffort }, cancellationToken);
    }

    /// <summary>读取仿真活跃度配置。</summary>
    public Task<ActivityConfig?> GetActivityConfigAsync(CancellationToken cancellationToken = default)
        => GetAsync<ActivityConfig>("/api/config/activity", cancellationToken);

    /// <summary>发送一条测试消息。</summary>
    /// <param name="target">目标（群号或 <c>private:QQ</c> 形式）。</param>
    /// <param name="text">消息内容。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>是否发送成功。</returns>
    public Task<bool> SendTestAsync(string target, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return PostAsync("/api/test-send", new { target, text }, cancellationToken);
    }

    /// <summary>GET 并反序列化；任何失败都返回 <c>null</c>。</summary>
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
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
        {
            // 网络与解析错误都属于"读不到"，由界面显示为未知状态。
            return null;
        }
    }

    /// <summary>POST JSON；任何失败都返回 <c>false</c>。</summary>
    private async Task<bool> PostAsync<T>(string path, T payload, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .PostAsJsonAsync(path, payload, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
