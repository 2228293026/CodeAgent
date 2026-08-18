using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CodeAgent.Providers;

/// <summary>
/// OpenAI 兼容协议 Provider：适用于 OpenAI、DeepSeek、通义千问（DashScope 兼容模式）、
/// Ollama（/v1）、Moonshot、智谱 GLM 等所有实现 chat/completions 风格 API 的服务。
/// </summary>
public sealed class OpenAiProvider : IAgentProvider
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-4o";
    /// <summary>OpenAI 推理系列（o1/o3/o4/gpt-5）：不接受 temperature 与 max_tokens，
    /// 需改用 max_completion_tokens 且不传温度，否则 400（模型名先去掉厂商前缀再判断）。</summary>
    private bool IsReasoningSeries
    {
        get
        {
            var name = _model.ToLowerInvariant();
            var slash = name.LastIndexOf('/');
            if (slash >= 0)
                name = name[(slash + 1)..];
            return name.StartsWith("o1") || name.StartsWith("o3") || name.StartsWith("o4") || name.StartsWith("gpt-5");
        }
    }

    /// <summary>按模型系列写入输出上限/温度（推理系列用 max_completion_tokens 且不带 temperature）。</summary>
    private void ApplyLimits(JsonObject payload)
    {
        if (IsReasoningSeries)
            payload["max_completion_tokens"] = _maxTokens;
        else
        {
            payload["temperature"] = _temperature;
            payload["max_tokens"] = _maxTokens;
        }
    }

    public const string DefaultApiKeyEnv = "OPENAI_API_KEY";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly int _maxTokens;
    private readonly double _temperature;

    /// <summary>未注入 HttpClient 时的共享实例：/model 每次切换都会新建 Provider，
    /// 逐实例 new HttpClient 会各自持有连接池不释放（套接字耗尽），必须全局复用。</summary>
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(300) };

    public OpenAiProvider(ProviderOptions opts, HttpClient? http = null)
    {
        _baseUrl = (string.IsNullOrWhiteSpace(opts.BaseUrl) ? DefaultBaseUrl : opts.BaseUrl).TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(opts.Model) ? DefaultModel : opts.Model;
        _maxTokens = opts.MaxTokens <= 0 ? 8192 : opts.MaxTokens;
        _temperature = opts.Temperature;
        _apiKey = ResolveApiKey(opts, DefaultApiKeyEnv);
        _http = http ?? SharedHttp;
    }

    public string Name => "openai";

    public static string ResolveApiKey(ProviderOptions opts, string defaultEnv)
    {
        var key = opts.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            var env = string.IsNullOrWhiteSpace(opts.ApiKeyEnv) ? defaultEnv : opts.ApiKeyEnv!;
            key = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(
                    $"未找到 API Key：请在 codeagent.json 中设置 apiKey，或设置环境变量 {env}。（可先运行 codeagent --setup 配置供应商）");
        }
        return key.Trim();
    }

    /// <summary>auto 思考强度的探测结果缓存（key = baseUrl|model，值为升序档位列表），避免每轮调用重复探测。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>?> ReasoningSupportCache = new();

    /// <summary>解析思考强度：auto 时探测模型支持的档位（结果缓存），
    /// 取最高可用档（供应商声明支持 high 就用 high，只支持 low 就用 low）；不支持/无法判断则按 off 处理。
    /// 探测失败（网络/5xx）不缓存：瞬时断网回退前缀表的结果若被缓存，
    /// 断网恢复后 auto 仍按旧结果处理直到重启。</summary>
    private async Task<string> ResolveEffortAsync(string thinkingEffort, CancellationToken ct)
    {
        if (thinkingEffort != "auto")
            return thinkingEffort;
        var key = _baseUrl + "|" + _model;
        if (!ReasoningSupportCache.TryGetValue(key, out var efforts))
        {
            var ok = true;
            try
            {
                efforts = await ProbeEffortsAsync(_model, ct);
            }
            catch
            {
                ok = false; // 探测失败：回退前缀表，但不进缓存（下次调用重试探测）
                efforts = KnownReasoningModels.TryGet(_model);
            }
            if (ok)
                ReasoningSupportCache[key] = efforts;
        }
        return efforts is { Count: > 0 } ? efforts[^1] : "off";
    }

    public async Task<ProviderResponse> ChatAsync(
        IReadOnlyList<ProviderMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        string thinkingEffort,
        CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = BuildMessages(messages),
        };
        ApplyLimits(payload);
        if (tools.Count > 0)
        {
            // 空工具列表整体省略（/compact 摘要调用）：tools:[] 与 tool_choice 同时省略，
            // 兼容性最好（部分 OpenAI 兼容服务对空数组报错）
            payload["tools"] = BuildTools(tools);
            payload["tool_choice"] = "auto";
        }
        var effort = await ResolveEffortAsync(thinkingEffort, ct);
        if (effort is "low" or "medium" or "high")
            payload["reasoning_effort"] = effort; // OpenAI o 系列 / OpenRouter 推理模型；off/auto 不支持时不发

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = JsonContent.Create(payload);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("请求超时（300s），请检查网络或 baseUrl。");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException($"无法连接 API（{_baseUrl}）: {ex.Message}", ex) { Retryable = true };
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ProviderException($"OpenAI 兼容 API 返回 {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 800)}")
            {
                StatusCode = (int)resp.StatusCode,
                Retryable = (int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500,
            };

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ProviderException($"响应不是合法 JSON: {Truncate(body, 300)}", ex);
        }

        // choices 可能为空数组（部分网关在仅带 usage 或无内容时返回 []），
        // 直接 [0] 会对空数组抛 ArgumentOutOfRangeException
        var choicesArr = root?["choices"] as JsonArray;
        var choice = choicesArr is { Count: > 0 } ? choicesArr[0]?["message"] : null;
        // content 可能是分块数组（部分兼容服务返回 [{"type":"text","text":…}]）：
        // 必须先判数组再取字符串——对 JsonArray 调 GetValue<string> 会直接抛 InvalidOperationException，
        // 原来的「先取字符串再 if 判数组」写法让数组分支永远不可达
        string text;
        if (choice?["content"] is JsonArray parts)
            text = string.Join("", parts.Select(b => b?["text"]?.GetValue<string>() ?? ""));
        else
            text = choice?["content"]?.GetValue<string>() ?? "";

        var toolCalls = new List<ToolCall>();
        var arr = choice?["tool_calls"]?.AsArray();
        if (arr is not null)
        {
            foreach (var tc in arr)
            {
                toolCalls.Add(new ToolCall
                {
                    Id = tc?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                    Name = tc?["function"]?["name"]?.GetValue<string>() ?? "unknown",
                    ArgumentsJson = tc?["function"]?["arguments"]?.GetValue<string>() ?? "{}",
                });
            }
        }

        int? inTok = root?["usage"]?["prompt_tokens"]?.GetValue<int>();
        int? outTok = root?["usage"]?["completion_tokens"]?.GetValue<int>();
        int? cachedTok = root?["usage"]?["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>();
        var finish = choicesArr is { Count: > 0 } ? choicesArr[0]?["finish_reason"]?.GetValue<string>() : null; // "length" = 被 max_tokens 截断

        return new ProviderResponse
        {
            Text = string.IsNullOrWhiteSpace(text) ? null : text,
            ToolCalls = toolCalls,
            InputTokens = inTok,
            OutputTokens = outTok,
            CachedTokens = cachedTok,
            FinishReason = finish,
        };
    }

    /// <summary>
    /// SSE 流式版本：文本增量通过 onText 实时回调；工具调用按 index 累积增量后整体返回。
    /// </summary>
    public async Task<ProviderResponse> ChatStreamAsync(
        IReadOnlyList<ProviderMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        string thinkingEffort,
        Action<string>? onText,
        Action<string>? onReasoning,
        Action<string>? onToolFragment,
        CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = BuildMessages(messages),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        ApplyLimits(payload);
        if (tools.Count > 0)
        {
            // 空工具列表整体省略（/compact 摘要调用）：tools:[] 与 tool_choice 同时省略
            payload["tools"] = BuildTools(tools);
            payload["tool_choice"] = "auto";
        }
        var effort = await ResolveEffortAsync(thinkingEffort, ct);
        if (effort is "low" or "medium" or "high")
            payload["reasoning_effort"] = effort; // OpenAI o 系列 / OpenRouter 推理模型；off/auto 不支持时不发

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = JsonContent.Create(payload);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("请求超时（300s），请检查网络或 baseUrl。");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException($"无法连接 API（{_baseUrl}）: {ex.Message}", ex) { Retryable = true };
        }

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new ProviderException($"OpenAI 兼容 API 返回 {(int)resp.StatusCode}: {Truncate(err, 800)}")
            {
                StatusCode = (int)resp.StatusCode,
                Retryable = (int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500,
            };
        }

        var text = new StringBuilder();
        var toolAccum = new Dictionary<int, StreamToolAccum>();
        int? inTok = null, outTok = null, cachedTok = null;
        string? finishReason = null; // 最后一个非空 finish_reason（"length" = 输出被截断）

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;
            if (data == "[DONE]")
                break;

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (root?["error"] is JsonObject errObj)
            {
                var errType = errObj["type"]?.GetValue<string>() ?? "";
                var errCode = errObj["code"] is JsonValue errVal && errVal.TryGetValue<int>(out var c) ? c : (int?)null;
                throw new ProviderException(
                    $"流式响应中断: {errObj["message"]?.GetValue<string>() ?? Truncate(errObj.ToJsonString(), 300)}")
                {
                    StatusCode = errCode,
                    // 限流类错误允许自动重试（Agent 只在尚未输出任何文本时重试，不会重复打印）
                    Retryable = errCode == 429 || errType.Contains("rate", StringComparison.OrdinalIgnoreCase)
                                            || errType.Contains("overloaded", StringComparison.OrdinalIgnoreCase),
                };
            }

            // 同非流式：空 choices 数组直接 [0] 会抛 ArgumentOutOfRangeException
            // （new-api 等网关在结束/usage chunk 返回 {"choices":[],"usage":…}）
            var choicesArr = root?["choices"] as JsonArray;
            var delta = choicesArr is { Count: > 0 } ? choicesArr[0]?["delta"] : null;

            // usage 可能随任意 chunk 到达（hitmargin 在带 delta 的最后一个 chunk 里返回 usage）
            if (root?["usage"] is JsonObject u)
            {
                if (u["prompt_tokens"] is not null)
                    inTok = u["prompt_tokens"]!.GetValue<int>();
                if (u["completion_tokens"] is not null)
                    outTok = u["completion_tokens"]!.GetValue<int>();
                if (u["prompt_tokens_details"]?["cached_tokens"] is not null)
                    cachedTok = u["prompt_tokens_details"]!["cached_tokens"]!.GetValue<int>();
            }

            // finish_reason 随结束 chunk 到达（可能不带 delta）：取最后一个非空值
            var fr = choicesArr is { Count: > 0 } ? choicesArr[0]?["finish_reason"]?.GetValue<string>() : null;
            if (!string.IsNullOrEmpty(fr))
                finishReason = fr;
            if (delta is null)
                continue;

            // 思考内容（DeepSeek-R1 用 reasoning_content，OpenRouter 用 reasoning）
            var reasoning = delta["reasoning_content"] ?? delta["reasoning"];
            if (reasoning is JsonValue rv && rv.TryGetValue<string>(out var r) && r.Length > 0)
                onReasoning?.Invoke(r);

            var refusal = delta["refusal"];
            // o 系列安全拒绝走 delta.refusal：不处理会被静默丢弃，回合以「模型未返回内容」结束
            if (refusal is JsonValue refv && refv.TryGetValue<string>(out var refusalText) && refusalText.Length > 0)
            {
                text.Append(refusalText);
                onText?.Invoke(refusalText);
            }
            var content = delta["content"];
            if (content is JsonValue cv && cv.TryGetValue<string>(out var t) && t.Length > 0)
            {
                text.Append(t);
                onText?.Invoke(t);
            }

            var tcs = delta["tool_calls"]?.AsArray();
            if (tcs is not null)
            {
                foreach (var tc in tcs)
                {
                    var index = tc?["index"]?.GetValue<int>() ?? 0;
                    if (!toolAccum.TryGetValue(index, out var acc))
                    {
                        acc = new StreamToolAccum();
                        toolAccum[index] = acc;
                    }

                    var id = tc?["id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id) && acc.Id.Length == 0)
                        acc.Id = id;
                    var name = tc?["function"]?["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name) && acc.Name.Length == 0)
                        acc.Name = name;
                    var frag = tc?["function"]?["arguments"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(frag))
                    {
                        acc.Args.Append(frag);
                        onToolFragment?.Invoke(frag); // 工具参数计入 ↑ tokens
                    }
                }
            }
        }

        var toolCalls = toolAccum
            .OrderBy(kv => kv.Key)
            .Select(kv => new ToolCall
            {
                Id = kv.Value.Id.Length > 0 ? kv.Value.Id : Guid.NewGuid().ToString("N"),
                Name = kv.Value.Name.Length > 0 ? kv.Value.Name : "unknown",
                ArgumentsJson = kv.Value.Args.Length > 0 ? kv.Value.Args.ToString() : "{}",
            })
            .ToList();

        return new ProviderResponse
        {
            Text = text.Length > 0 ? text.ToString() : null,
            ToolCalls = toolCalls,
            InputTokens = inTok,
            OutputTokens = outTok,
            CachedTokens = cachedTok,
            FinishReason = finishReason,
        };
    }

    /// <summary>列出 OpenAI 兼容服务的可用模型（GET /models）。</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var m in await FetchModelsArrayAsync(ct) ?? [])
        {
            var id = m?["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id))
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>从 /models 元数据探测模型上下文窗口：OpenRouter 的 context_length /
    /// top_provider.context_length、LM Studio 的 max_context_length 等。
    /// 标准 OpenAI 协议不带窗口信息时返回 null（由状态栏回退到内置模型表或纯数字）。</summary>
    public async Task<int?> GetContextWindowAsync(string model, CancellationToken ct)
    {
        try
        {
            var arr = await FetchModelsArrayAsync(ct);
            foreach (var m in arr ?? [])
            {
                // 只看对象项：null/标量项没有元数据，且对非对象索引会抛异常
                if (m is not JsonObject entry)
                    continue;
                if (!string.Equals(entry["id"]?.GetValue<string>(), model, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var key in new[] { "context_length", "context_window", "max_context_length", "max_model_len", "max_input_tokens", "input_token_limit" })
                    if (entry[key] is JsonValue v && v.TryGetValue<int>(out var n) && n > 0)
                        return n;
                if (entry["top_provider"]?["context_length"] is JsonValue tv && tv.TryGetValue<int>(out var n2) && n2 > 0)
                    return n2;
                return null; // 找到模型但元数据无窗口字段
            }
            return null;
        }
        catch
        {
            return null; // 探测失败不影响主流程
        }
    }

    /// <summary>探测模型支持的推理档位：优先 /models 元数据（OpenRouter 等网关带 reasoning.effort 字段，
    /// 值为 true 的键即支持的档位，按 low→high 升序返回）；元数据无能力信息时回退到内置模型名前缀表；
    public async Task<IReadOnlyList<string>?> GetSupportedEffortsAsync(string model, CancellationToken ct)
    {
        try
        {
            return await ProbeEffortsAsync(model, ct);
        }
        catch
        {
            // 探测失败（网络/鉴权/非 JSON）：不影响主流程，回退前缀表
            return KnownReasoningModels.TryGet(model);
        }
    }

    /// <summary>真实探测（不吞异常）：ResolveEffortAsync 据此区分「探测完成」与「失败回退」，
    /// 失败结果不进缓存（否则瞬时断网让 auto 整个会话按回退值处理）。</summary>
    internal async Task<IReadOnlyList<string>?> ProbeEffortsAsync(string model, CancellationToken ct)
    {
        var arr = await FetchModelsArrayAsync(ct);
        foreach (var m in arr ?? [])
        {
            if (m is not JsonObject entry)
                continue;
            if (!string.Equals(entry["id"]?.GetValue<string>(), model, StringComparison.OrdinalIgnoreCase))
                continue;
            // OpenRouter 等网关在 reasoning.effort 里显式声明支持的档位
            if (entry["reasoning"]?["effort"] is JsonObject effort)
            {
                var levels = new List<string>();
                foreach (var lvl in new[] { "low", "medium", "high" }) // 升序
                    if (effort[lvl] is JsonValue v && v.TryGetValue<bool>(out var b) && b)
                        levels.Add(lvl);
                return levels.Count > 0 ? levels : null; // effort 存在但全 false → 不支持
            }
            return KnownReasoningModels.TryGet(model); // 找到模型但元数据无 effort 字段 → 回退前缀表
        }
        return KnownReasoningModels.TryGet(model);
    }

    /// <summary>GET /models 的 data 数组；失败抛 ProviderException（与 ListModelsAsync 的错误契约一致）。</summary>
    private async Task<JsonArray?> FetchModelsArrayAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ProviderException($"模型列表接口返回 {(int)resp.StatusCode}: {Truncate(body, 400)}");
        return JsonNode.Parse(body)?["data"]?.AsArray();
    }

    private static JsonArray BuildMessages(IReadOnlyList<ProviderMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case MessageRole.System:
                    arr.Add(new JsonObject { ["role"] = "system", ["content"] = m.Content ?? "" });
                    break;

                case MessageRole.User:
                    arr.Add(new JsonObject { ["role"] = "user", ["content"] = m.Content ?? "" });
                    break;

                case MessageRole.Assistant:
                    {
                        var obj = new JsonObject { ["role"] = "assistant" };
                        // 带 tool_calls 且无文本时传 null content：部分 OpenAI 兼容 API 会拒绝空字符串
                        if (m.ToolCalls is { Count: > 0 } && string.IsNullOrEmpty(m.Content))
                            obj["content"] = null;
                        else
                            obj["content"] = m.Content ?? "";
                        if (m.ToolCalls is { Count: > 0 })
                        {
                            var calls = new JsonArray();
                            foreach (var tc in m.ToolCalls)
                            {
                                calls.Add(new JsonObject
                                {
                                    ["id"] = tc.Id,
                                    ["type"] = "function",
                                    ["function"] = new JsonObject
                                    {
                                        ["name"] = tc.Name,
                                        ["arguments"] = tc.ArgumentsJson,
                                    },
                                });
                            }
                            obj["tool_calls"] = calls;
                        }
                        arr.Add(obj);
                        break;
                    }

                case MessageRole.Tool:
                    arr.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = m.ToolCallId ?? "",
                        ["content"] = m.Content ?? "",
                    });
                    break;
            }
        }
        return arr;
    }

    private static JsonArray BuildTools(IReadOnlyList<ToolSpec> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone(),
                },
            });
        }
        return arr;
    }

    internal static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n…(共 {s.Length} 字符，已截断)";
}
