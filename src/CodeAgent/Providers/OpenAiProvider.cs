using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAgent.Providers;

/// <summary>
/// OpenAI 兼容协议 Provider：适用于 OpenAI、DeepSeek、通义千问（DashScope 兼容模式）、
/// Ollama（/v1）、Moonshot、智谱 GLM 等所有实现 chat/completions 风格 API 的服务。
/// </summary>
public sealed class OpenAiProvider : IAgentProvider
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-4o";
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
            ["tools"] = BuildTools(tools),
            ["tool_choice"] = "auto",
            ["temperature"] = _temperature,
            ["max_tokens"] = _maxTokens,
        };
        if (thinkingEffort != "off")
            payload["reasoning_effort"] = thinkingEffort; // OpenAI o 系列 / OpenRouter 推理模型

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

        var choice = root?["choices"]?[0]?["message"];
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

        return new ProviderResponse
        {
            Text = string.IsNullOrWhiteSpace(text) ? null : text,
            ToolCalls = toolCalls,
            InputTokens = inTok,
            OutputTokens = outTok,
            CachedTokens = cachedTok,
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
            ["tools"] = BuildTools(tools),
            ["tool_choice"] = "auto",
            ["temperature"] = _temperature,
            ["max_tokens"] = _maxTokens,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (thinkingEffort != "off")
            payload["reasoning_effort"] = thinkingEffort; // OpenAI o 系列 / OpenRouter 推理模型

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

            var delta = root?["choices"]?[0]?["delta"];

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

            if (delta is null)
                continue;

            // 思考内容（DeepSeek-R1 用 reasoning_content，OpenRouter 用 reasoning）
            var reasoning = delta["reasoning_content"] ?? delta["reasoning"];
            if (reasoning is not null)
            {
                var r = reasoning.GetValue<string>() ?? "";
                if (r.Length > 0)
                    onReasoning?.Invoke(r);
            }

            var content = delta["content"];
            if (content is not null)
            {
                var t = content.GetValue<string>() ?? "";
                if (t.Length > 0)
                {
                    text.Append(t);
                    onText?.Invoke(t);
                }
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
        };
    }

    /// <summary>列出 OpenAI 兼容服务的可用模型（GET /models）。</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ProviderException($"模型列表接口返回 {(int)resp.StatusCode}: {Truncate(body, 400)}");

        var ids = new List<string>();
        foreach (var m in JsonNode.Parse(body)?["data"]?.AsArray() ?? [])
        {
            var id = m?["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id))
                ids.Add(id);
        }
        return ids;
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
