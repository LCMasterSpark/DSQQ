using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSQQ.Models;

namespace DSQQ.Services;

// qq-bridge 控制台（默认 127.0.0.1:3100）的 HTTP 客户端。
// 桥接自带控制台，其 HTTP API 覆盖了 Web 面板的全部功能。
// 本程序的原生界面就建立在这套 API 上，不内嵌网页。
// 注意：控制台鉴权是否开启由桥接配置决定；本程序依赖"已关闭鉴权或本机可信"的前提。
public sealed class BridgeApiClient : IDisposable
{
    // 反序列化选项：忽略大小写，容忍后端增删字段。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    // 初始化客户端。
    // 参数 baseUri：控制台基地址，例如 http://127.0.0.1:3100。
    // 参数 timeout：请求超时。
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

    // 控制台基地址。
    public Uri BaseUri { get; }

    // 最近一次读取失败的原因（成功时为 null）。
    // 界面用它把"读不到"细化成具体原因，而不是一律显示"服务未启动"。
    public string? LastError { get; private set; }

    // 读取运行状态。
    // 参数 cancellationToken：取消标记。
    // 返回：状态对象；请求失败或响应不可解析时返回 null。
    public Task<BridgeStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetAsync<BridgeStatus>("/api/status", cancellationToken);

    // 读取白名单与黑名单。
    public Task<BridgeWhitelist?> GetWhitelistAsync(CancellationToken cancellationToken = default)
        => GetAsync<BridgeWhitelist>("/api/whitelist", cancellationToken);

    // 写入白名单。
    // 参数 allowPrivate：允许的私聊号码。
    // 参数 allowGroups：允许的群号。
    // 参数 ownerQQ：管理员 QQ 号；为 null 表示不改动。
    // 参数 cancellationToken：取消标记。
    // 返回：是否写入成功。
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

    // 读取机器人所在的所有群（带群名）。
    public async Task<IReadOnlyList<QqGroup>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<QqGroupList>("/api/qq/groups", cancellationToken).ConfigureAwait(false);
        return response?.Groups ?? [];
    }

    // 读取会话映射。
    public async Task<IReadOnlyList<BridgeSessionEntry>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<BridgeSessions>("/api/sessions", cancellationToken).ConfigureAwait(false);
        return response?.Sessions ?? [];
    }

    // 读取 token 与花费汇总。
    public Task<TokenSummary?> GetTokenSummaryAsync(CancellationToken cancellationToken = default)
        => GetAsync<TokenSummary>("/api/tokens/summary", cancellationToken);

    // 读取人设配置。
    public Task<PersonaResponse?> GetPersonaAsync(CancellationToken cancellationToken = default)
        => GetAsync<PersonaResponse>("/api/config/persona", cancellationToken);

    // 保存人设配置。
    // 参数 persona：人设内容。
    // 参数 cancellationToken：取消标记。
    // 返回：是否保存成功（包含改写规则意图的内容会被后端拒绝）。
    public Task<bool> SavePersonaAsync(PersonaConfig persona, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        return PostAsync("/api/config/persona", persona, cancellationToken);
    }

    // 读取每日配额配置。
    public Task<QuotaConfig?> GetQuotaAsync(CancellationToken cancellationToken = default)
        => GetAsync<QuotaConfig>("/api/config/quota", cancellationToken);

    // 保存每日配额配置。
    public Task<bool> SaveQuotaAsync(QuotaConfig quota, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quota);
        return PostAsync("/api/config/quota", quota, cancellationToken);
    }

    // 读取模型与推理强度设置。
    public Task<ModelSelection?> GetModelSelectionAsync(CancellationToken cancellationToken = default)
        => GetAsync<ModelSelection>("/api/dsh/model-selection", cancellationToken);

    // 读取模型目录：可选的模型清单与推理强度说明（切换下拉框的数据源）。
    public Task<DshModelCatalog?> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        => GetAsync<DshModelCatalog>("/api/dsh/model", cancellationToken);

    // 保存模型选择。
    // 参数 enabled：是否启用模型接管。
    // 参数 model：模型标识；enabled 为 true 时必填且要匹配 [A-Za-z0-9._:-]{1,64}。
    // 参数 reasoningEffort：思考强度；enabled 为 true 时必须是 low / high / max 之一。
    // 返回：是否保存成功。
    //
    // 注意端点是 model-selection 而不是 model —— 后者是 GET（读目录），
    // 且桥接在 enabled=true 时要求 model 与 reasoningEffort **同时**给出，缺一即 400。
    public Task<bool> SaveModelSelectionAsync(
        bool enabled,
        string model,
        string reasoningEffort,
        CancellationToken cancellationToken = default)
    {
        if (enabled)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            ArgumentException.ThrowIfNullOrWhiteSpace(reasoningEffort);
        }

        return PostAsync("/api/dsh/model-selection", new { enabled, model, reasoningEffort }, cancellationToken);
    }

    // 切换运行模式。
    // 参数 mode：目标模式（chat / closed-agent / reserved / reserved2）。
    // 参数 cancellationToken：取消标记。
    // 返回：是否切换成功。
    public Task<bool> SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        return PostAsync("/api/mode", new { mode }, cancellationToken);
    }

    // 设置 DSH 思考强度。
    // 参数 reasoningEffort：强度档位（low / high / max）。
    // 参数 cancellationToken：取消标记。
    // 返回：是否设置成功。
    // 该设置会同时写入 DSH 全局默认，对已有会话于下一条消息生效（无需重启）。
    public Task<bool> SetReasoningEffortAsync(string reasoningEffort, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasoningEffort);
        return PostAsync("/api/dsh/effort", new { reasoningEffort }, cancellationToken);
    }

    // 读取仿真活跃度配置。
    //
    // 注意响应有一层 activity 包装：{"ok":true,"activity":{...}}。
    // 早先直接按 ActivityConfig 反序列化顶层，字段一个都对不上，
    // 于是所有属性取默认值 0 —— 界面表现为"输入框全部显示 0"，
    // 而不会报任何错。POST 侧不需要包装（见 SaveActivityAsync）。
    public async Task<ActivityConfig?> GetActivityConfigAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<ActivityResponse>("/api/config/activity", cancellationToken)
            .ConfigureAwait(false);

        return response?.Activity;
    }

    // 保存活跃度与发送节奏（部分更新：只提交改动过的字段，未提交的由桥接保持现状）。
    // 返回：null 表示成功；否则为桥接给出的具体校验错误文本。
    //
    // 这里特意不用 bool：桥接对这个端点有一整套范围与联动校验
    //（例如"最短潜水不能大于最长潜水"），只回一个 false 会让用户完全不知道哪里填错了。
    public async Task<string?> SaveActivityAsync(
        IReadOnlyDictionary<string, object> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/config/activity", changes, JsonOptions, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ExtractError(body) ?? $"桥接返回 {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ex is TaskCanceledException ? "请求超时（桥接可能未运行）" : $"无法连接桥接：{ex.Message}";
        }
    }

    // 从 {"ok":false,"error":"..."} 里取出错误文本；解析不出来时原样返回，便于排查。
    private static string? ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应：当作原文返回。
        }

        return body.Length > 300 ? body[..300] : body;
    }

    // 发送一条测试消息。
    // 参数 target：目标（群号或 private:QQ 形式）。
    // 参数 text：消息内容。
    // 参数 cancellationToken：取消标记。
    // 返回：是否发送成功。
    public Task<bool> SendTestAsync(string target, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return PostAsync("/api/test-send", new { target, text }, cancellationToken);
    }

    // GET 并反序列化；任何失败都返回 null。
    // 读取角色（人格）列表与当前启用项。
    public Task<RoleListResponse?> GetRolesAsync(CancellationToken cancellationToken = default)
        => GetAsync<RoleListResponse>("/api/roles", cancellationToken);

    // 读取单个角色的正文。
    public Task<RoleContentResponse?> GetRoleContentAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return GetAsync<RoleContentResponse>($"/api/roles/content?name={Uri.EscapeDataString(name)}", cancellationToken);
    }

    // 保存角色正文（不存在则按新建处理）。
    public Task<bool> SaveRoleAsync(string name, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return PostAsync("/api/roles/update", new { name, content }, cancellationToken);
    }

    // 重命名角色。
    public Task<bool> RenameRoleAsync(string name, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        return PostAsync("/api/roles/rename", new { name, newName }, cancellationToken);
    }

    // 删除角色。
    public Task<bool> DeleteRoleAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return PostAsync("/api/roles/delete", new { name }, cancellationToken);
    }

    // 启用或清除当前角色（传空字符串表示清除）。
    //
    // ⚠️ 桥接的 /api/role 读的是 body.role，不是 body.name。
    // 传 name 时它会落进 else 分支执行"清除角色"，却仍返回 {"ok":true} ——
    // 表现为"点了启用、界面提示已启用，但角色其实被清掉了"，属于静默失效。
    public Task<bool> SetCurrentRoleAsync(string name, CancellationToken cancellationToken = default)
        => PostAsync("/api/role", new { role = name }, cancellationToken);

    // 读取仿真提示词（预设内置的那一层）。
    public Task<SimPromptResponse?> GetSimPromptAsync(string preset, CancellationToken cancellationToken = default)    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preset);
        return GetAsync<SimPromptResponse>($"/api/preset/sim-prompt?preset={Uri.EscapeDataString(preset)}", cancellationToken);
    }

    // 保存仿真提示词；桥接会先做安全不变量校验，不合规会被拒绝。
    public Task<bool> SaveSimPromptAsync(string preset, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preset);
        return PostAsync("/api/preset/sim-prompt", new { preset, content }, cancellationToken);
    }

    // 读取黑话词库。
    public Task<SlangResponse?> GetSlangAsync(CancellationToken cancellationToken = default)
        => GetAsync<SlangResponse>("/api/slang", cancellationToken);

    // 批量确认候选词条。
    public Task<bool> ConfirmSlangAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
        => PostAsync("/api/slang/batch-confirm", new { ids }, cancellationToken);

    // 批量拒绝候选词条。
    public Task<bool> RejectSlangAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
        => PostAsync("/api/slang/batch-reject", new { ids }, cancellationToken);

    // 批量删除词条。
    public Task<bool> DeleteSlangAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
        => PostAsync("/api/slang/batch-delete", new { ids }, cancellationToken);

    // 触发一次黑话提取。
    public Task<bool> ExtractSlangAsync(CancellationToken cancellationToken = default)
        => PostAsync("/api/slang/extract", new { }, cancellationToken);

    // ── 二代仿真 ──

    // 读取所有会话的二代状态（唤醒配置、未读、上次唤醒原因）。
    public Task<SocialV2StatesResponse?> GetSocialV2StatesAsync(CancellationToken cancellationToken = default)
        => GetAsync<SocialV2StatesResponse>("/api/socialV2/states", cancellationToken);

    // 读取单个会话的二代状态。
    public Task<SocialV2Conversation?> GetSocialV2StateAsync(
        string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return GetAsync<SocialV2Conversation>(
            $"/api/socialV2/state?key={Uri.EscapeDataString(key)}", cancellationToken);
    }

    // 读取某会话的记忆（formatted 是已渲染好的原文）。
    public Task<SocialV2MemoryResponse?> GetSocialV2MemoryAsync(
        string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return GetAsync<SocialV2MemoryResponse>(
            $"/api/socialV2/memory?key={Uri.EscapeDataString(key)}", cancellationToken);
    }

    // 读取工具调用日志。这是排查"她为什么不说话"的第一入口 ——
    // 能看到她到底调了哪些工具、哪些失败了。
    public Task<SocialV2ToolLogResponse?> GetSocialV2ToolLogAsync(
        int limit = 200, CancellationToken cancellationToken = default)
        => GetAsync<SocialV2ToolLogResponse>($"/api/socialV2/tool-log?limit={limit}", cancellationToken);

    // 清空工具调用日志。
    public Task<bool> ClearSocialV2ToolLogAsync(CancellationToken cancellationToken = default)
        => PostAsync("/api/socialV2/tool-log/clear", new { }, cancellationToken);

    // 读取二代仿真的活动状态（是否被暂停）。
    public Task<SocialV2ActivityResponse?> GetSocialV2ActivityAsync(CancellationToken cancellationToken = default)
        => GetAsync<SocialV2ActivityResponse>("/api/socialV2/activity", cancellationToken);

    // 读取反馈条目。
    public Task<SocialV2FeedbackResponse?> GetSocialV2FeedbackAsync(CancellationToken cancellationToken = default)
        => GetAsync<SocialV2FeedbackResponse>("/api/socialV2/feedback", cancellationToken);

    // 读取挂起审批 / 提问。
    // 只读 —— 应答由 QQ 消息完成（管理员在对应会话回复"通过/拒绝"或答案）。
    public Task<PendingResponse?> GetPendingAsync(CancellationToken cancellationToken = default)
        => GetAsync<PendingResponse>("/api/pending", cancellationToken);

    // 读取消耗走势（按时间分桶）。
    public Task<TokenSeries?> GetTokenSeriesAsync(CancellationToken cancellationToken = default)
        => GetAsync<TokenSeries>("/api/tokens/series", cancellationToken);

    // 读取某会话的逐轮花费；key 形如 group:群号 或 private:QQ号。
    public Task<TokenTurns?> GetTokenTurnsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return GetAsync<TokenTurns>($"/api/tokens/turns?key={Uri.EscapeDataString(key)}", cancellationToken);
    }

    // 设置角色模式：active（正常）或 silent（静默 —— 不回复群友，管理员私聊照常）。
    public Task<bool> SetRoleModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        return PostAsync("/api/role-mode", new { mode }, cancellationToken);
    }

    // 重启桥接进程。
    public Task<bool> RestartBridgeAsync(CancellationToken cancellationToken = default)
        => PostAsync("/api/restart", new { }, cancellationToken);

    // 重置指定会话的上下文（清除该会话的 DSH 会话映射）。
    public Task<bool> ResetSessionAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return PostAsync("/api/session/reset", new { key }, cancellationToken);
    }

    // 重置整个工作区（清空所有会话映射）。
    public Task<bool> ResetWorkspaceAsync(CancellationToken cancellationToken = default)
        => PostAsync("/api/workspace/reset", new { }, cancellationToken);

    // 读取会话模式。
    public Task<SessionModeResponse?> GetSessionModeAsync(CancellationToken cancellationToken = default)
        => GetAsync<SessionModeResponse>("/api/config/session-mode", cancellationToken);

    // 设置会话模式：shared（共享，跨群连续记忆）或 per-conversation（各自独立）。
    // 切换会回收并重建各会话，所以第一条消息会稍慢。
    public Task<bool> SetSessionModeAsync(string sessionMode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionMode);
        return PostAsync("/api/config/session-mode", new { sessionMode }, cancellationToken);
    }

    // 读取二代仿真配置（crossGroup 等开关在这里）。
    public Task<SocialV2ConfigResponse?> GetSocialV2ConfigAsync(CancellationToken cancellationToken = default)
        => GetAsync<SocialV2ConfigResponse>("/api/socialV2/config", cancellationToken);

    // 保存二代仿真配置。
    // 桥接侧是**读取-合并-写入**语义：只提交要改的字段即可，未提交的保持现状。
    // 因此传单个开关是安全的，不会把其它配置清掉。
    public Task<bool> SaveSocialV2ConfigAsync(object patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return PostAsync("/api/socialV2/config", patch, cancellationToken);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 记下状态码：404 说明端点不存在（版本不匹配），500 是桥接内部错误，
                // 与"没启动"是完全不同的两回事，界面上要能区分。
                LastError = $"{path} 返回 HTTP {(int)response.StatusCode}";
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                LastError = $"{path} 返回空响应";
                return null;
            }

            var parsed = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (parsed is null)
            {
                LastError = $"{path} 的响应无法解析为预期结构";
            }

            return parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
        {
            // 网络与解析错误都属于"读不到"，由界面显示为未知状态。
            // 但原因要留下来 —— 否则排查时只能看到一句"服务未启动"，
            // 而真实情况可能是超时、DNS、TLS 或端口不对。
            LastError = ex is TaskCanceledException or OperationCanceledException
                ? $"{path} 请求超时（{_http.Timeout.TotalSeconds:0} 秒）"
                : $"{path} 连接失败：{ex.Message}";
            return null;
        }
    }

    // POST JSON；任何失败都返回 false。
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

    public void Dispose() => _http.Dispose();
}
