using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAgent.Providers;

/// <summary>
/// Anthropic Claude Provider（messages API + tool use）。
/// Anthropic 要求消息角色必须交替、工具结果以 tool_result 内容块回传，
/// 这里把 Tool 角色的消息合并进相邻的 user 消息，并归一化连续同角色消息。
/// </summary>
public sealed class AnthropicProvider : IAgentProvider
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";
    public const string DefaultModel = "claude-sonnet-4-5";
    public const string DefaultApiKeyEnv = "ANTHROPIC_API_KEY";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly int _maxTokens;
    private readonly double _temperature;

    /// <summary>未注入 HttpClient 时的共享实例（同 OpenAiProvider：避免逐实例连接池泄漏）。</summary>
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(300) };

    public AnthropicProvider(ProviderOptions opts, HttpClient? http = null)
    {
        _baseUrl = (string.IsNullOrWhiteSpace(opts.BaseUrl) ? DefaultBaseUrl : opts.BaseUrl).TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(opts.Model) ? DefaultModel : opts.Model;
        _maxTokens = opts.MaxTokens <= 0 ? 8192 : opts.MaxTokens;
        _temperature = opts.Temperature;
        _apiKey = OpenAiProvider.ResolveApiKey(opts, DefaultApiKeyEnv);
        _http = http ?? SharedHttp;
    }

    public string Name => "anthropic";

    public async Task<ProviderResponse> ChatAsync(
        IReadOnlyList<ProviderMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        string thinkingEffort,
        CancellationToken ct)
    {
        var system = new StringBuilder();
        foreach (var m in messages.Where(m => m.Role == MessageRole.System))
            system.AppendLine(m.Content);

        var payload = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = _maxTokens,
            ["system"] = system.ToString().TrimEnd(),
            ["messages"] = BuildMessages(messages),
            ["tools"] = BuildTools(tools),
        };
        ApplyThinking(payload, thinkingEffort);

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/messages");
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
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
        {
            var errMsg = "";
            try { errMsg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? ""; }
            catch { /* 保留原始响应 */ }
            throw new ProviderException(
                $"Anthropic API 返回 {(int)resp.StatusCode} {resp.ReasonPhrase}: " +
                (errMsg.Length > 0 ? errMsg : OpenAiProvider.Truncate(body, 800)))
            {
                StatusCode = (int)resp.StatusCode,
                Retryable = (int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500,
            };
        }

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException ex)
        {
            throw new ProviderException($"响应不是合法 JSON: {OpenAiProvider.Truncate(body, 300)}", ex);
        }

        var text = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        var blocks = root?["content"]?.AsArray();
        if (blocks is not null)
        {
            foreach (var b in blocks)
            {
                var type = b?["type"]?.GetValue<string>();
                if (type == "text")
                    text.Append(b?["text"]?.GetValue<string>() ?? ""); // text 为 null/缺失时 Append(null) 会抛 ArgumentNullException
                else if (type == "tool_use")
                {
                    toolCalls.Add(new ToolCall
                    {
                        Id = b?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                        Name = b?["name"]?.GetValue<string>() ?? "unknown",
                        ArgumentsJson = b?["input"]?.ToJsonString() ?? "{}",
                    });
                }
            }
        }

        int? inTok = root?["usage"]?["input_tokens"]?.GetValue<int>();
        int? outTok = root?["usage"]?["output_tokens"]?.GetValue<int>();
        int? cachedTok = root?["usage"]?["input_tokens_details"]?["cache_read_input_tokens"]?.GetValue<int>();

        return new ProviderResponse
        {
            Text = text.Length == 0 ? null : text.ToString(),
            ToolCalls = toolCalls,
            InputTokens = inTok,
            OutputTokens = outTok,
            CachedTokens = cachedTok,
        };
    }

    /// <summary>
    /// SSE 流式版本：解析 content_block 系列事件，文本增量通过 onText 实时回调，
    /// tool_use 的 input JSON 按块累积后整体返回。
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
        var system = new StringBuilder();
        foreach (var m in messages.Where(m => m.Role == MessageRole.System))
            system.AppendLine(m.Content);

        var payload = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = _maxTokens,
            ["system"] = system.ToString().TrimEnd(),
            ["messages"] = BuildMessages(messages),
            ["tools"] = BuildTools(tools),
            ["stream"] = true,
        };
        ApplyThinking(payload, thinkingEffort);

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/messages");
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
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
            var body = await resp.Content.ReadAsStringAsync(ct);
            var errMsg = "";
            try { errMsg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? ""; }
            catch { /* 保留原始响应 */ }
            throw new ProviderException(
                $"Anthropic API 返回 {(int)resp.StatusCode}: " +
                (errMsg.Length > 0 ? errMsg : OpenAiProvider.Truncate(body, 800)))
            {
                StatusCode = (int)resp.StatusCode,
                Retryable = (int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500,
            };
        }

        var text = new StringBuilder();
        var toolAccum = new Dictionary<int, StreamToolAccum>();
        var done = false;
        var evt = "";
        int? inputTokens = null;
        int? outputTokens = null;
        int? cachedTokens = null;

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while (!done && (line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                evt = line["event:".Length..].Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            switch (evt)
            {
                case "content_block_start":
                {
                    var block = root?["content_block"];
                    var type = block?["type"]?.GetValue<string>();
                    var index = root?["index"]?.GetValue<int>() ?? 0;
                    if (type == "tool_use")
                    {
                        toolAccum[index] = new StreamToolAccum
                        {
                            Id = block?["id"]?.GetValue<string>() ?? "",
                            Name = block?["name"]?.GetValue<string>() ?? "",
                        };
                    }
                    break;
                }

                case "content_block_delta":
                {
                    var delta = root?["delta"];
                    var dtype = delta?["type"]?.GetValue<string>();
                    var index = root?["index"]?.GetValue<int>() ?? 0;
                    if (dtype == "thinking_delta")
                    {
                        // 思考内容（extended thinking）：实时回调，由 Agent 暗色显示
                        var t = delta?["thinking"]?.GetValue<string>() ?? "";
                        if (t.Length > 0)
                            onReasoning?.Invoke(t);
                    }
                    else if (dtype == "text_delta")
                    {
                        var t = delta?["text"]?.GetValue<string>() ?? "";
                        if (t.Length > 0)
                        {
                            text.Append(t);
                            onText?.Invoke(t);
                        }
                    }
                    else if (dtype == "input_json_delta")
                    {
                        var frag = delta?["partial_json"]?.GetValue<string>() ?? "";
                        if (frag.Length > 0 && toolAccum.TryGetValue(index, out var acc))
                        {
                            acc.Args.Append(frag);
                            onToolFragment?.Invoke(frag); // 工具参数计入 ↑ tokens
                        }
                    }
                    break;
                }

                case "message_start":
                    inputTokens = root?["message"]?["usage"]?["input_tokens"]?.GetValue<int>();
                    cachedTokens = root?["message"]?["usage"]?["input_tokens_details"]?["cache_read_input_tokens"]?.GetValue<int>();
                    break;

                case "message_delta":
                    outputTokens = root?["usage"]?["output_tokens"]?.GetValue<int>();
                    break;

                case "error":
                {
                    var msg = root?["error"]?["message"]?.GetValue<string>() ?? "未知错误";
                    throw new ProviderException($"Anthropic 流式错误: {msg}");
                }

                case "message_stop":
                    done = true;
                    break;
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
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedTokens = cachedTokens,
        };
    }

    /// <summary>列出 Anthropic 可用模型（GET /v1/models）。</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/v1/models");
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ProviderException($"模型列表接口返回 {(int)resp.StatusCode}: {OpenAiProvider.Truncate(body, 400)}");

        var ids = new List<string>();
        foreach (var m in JsonNode.Parse(body)?["data"]?.AsArray() ?? [])
        {
            var id = m?["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id))
                ids.Add(id);
        }
        return ids;
    }

    private JsonArray BuildMessages(IReadOnlyList<ProviderMessage> messages)
    {
        var arr = new JsonArray();

        void Append(JsonObject msg)
        {
            if (arr.Count > 0 && arr[^1]?["role"]?.GetValue<string>() == msg["role"]?.GetValue<string>())
            {
                // 合并连续同角色消息的内容块
                var last = arr[^1]!;
                var target = last["content"] as JsonArray;
                var source = msg["content"] as JsonArray;
                if (target is not null && source is not null)
                {
                    foreach (var block in source)
                    {
                        if (block is not null)
                            target.Add(block.DeepClone()); // 深拷贝：block 仍属于源消息，直接添加会报 "node already has a parent"
                    }
                }
                else if (target is not null && msg["content"] is JsonValue sv)
                {
                    target.Add(TextBlock(sv.GetValue<string>()));
                }
            }
            else
            {
                arr.Add(msg);
            }
        }

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case MessageRole.System:
                    break; // 已作为顶层 system 发送

                case MessageRole.User:
                {
                    var content = new JsonArray();
                    if (!string.IsNullOrEmpty(m.Content))
                        content.Add(TextBlock(m.Content));
                    if (content.Count > 0)
                        Append(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                }

                case MessageRole.Tool:
                {
                    var content = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = m.ToolCallId ?? "",
                            ["content"] = m.Content ?? "",
                            ["is_error"] = m.IsError,
                        },
                    };
                    Append(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                }

                case MessageRole.Assistant:
                {
                    var content = new JsonArray();
                    if (!string.IsNullOrEmpty(m.Content))
                        content.Add(TextBlock(m.Content));
                    foreach (var tc in m.ToolCalls ?? [])
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = tc.Id,
                            ["name"] = tc.Name,
                            ["input"] = ParseInput(tc.ArgumentsJson),
                        });
                    }
                    if (content.Count == 0)
                        content.Add(TextBlock(""));
                    Append(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;
                }
            }
        }

        return arr;
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    /// <summary>
    /// 应用思考强度。Anthropic 约束：thinking 启用时 temperature 必须省略（默认 1），
    /// 且 max_tokens 必须大于思考预算（至少多 1024）；这里按 max_tokens 收敛预算并省略 temperature。
    /// </summary>
    private void ApplyThinking(JsonObject payload, string thinkingEffort)
    {
        if (thinkingEffort == "off")
        {
            payload["temperature"] = _temperature;
            return;
        }
        var budget = thinkingEffort switch { "low" => 1024, "high" => 16384, _ => 4096 };
        budget = Math.Min(budget, Math.Max(1024, _maxTokens - 1024)); // 预算不超过 max_tokens - 1024
        payload["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
    }

    private static JsonNode ParseInput(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node is JsonObject ? node : new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static JsonArray BuildTools(IReadOnlyList<ToolSpec> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
        {
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.Parameters.DeepClone(),
            });
        }
        return arr;
    }
}
