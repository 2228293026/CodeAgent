using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAgent.Providers;

/// <summary>消息角色。</summary>
public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>模型请求调用工具的描述（一次工具调用）。</summary>
public sealed class ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>原始 JSON 参数字符串。</summary>
    public required string ArgumentsJson { get; init; }
}

/// <summary>与 Provider 无关的中间表示消息，由各 Provider 转换为自家 API 格式。</summary>
public sealed class ProviderMessage
{
    public MessageRole Role { get; init; }

    /// <summary>文本内容。Assistant 纯工具调用轮可为 null；Tool 结果始终有内容。</summary>
    public string? Content { get; init; }

    /// <summary>Assistant 侧的并行工具调用列表。</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>Tool 侧：对应的工具调用 Id。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Tool 侧：工具名（记录用）。</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool 侧：是否为错误结果。</summary>
    public bool IsError { get; init; }

    /// <summary>Assistant 侧：Anthropic extended thinking 的文本。
    /// thinking 启用 + 工具调用时，最后一轮 assistant 的 thinking 块必须原样回传（API 强制），
    /// 缺失会被 Anthropic 400 拒绝。OpenAI 侧忽略。</summary>
    public string? ThinkingText { get; init; }

    /// <summary>Assistant 侧：Anthropic thinking 块的签名（与 ThinkingText 成对回传）。</summary>
    public string? ThinkingSignature { get; init; }
}

/// <summary>Provider 一次调用的返回值。</summary>
public sealed class ProviderResponse
{
    public string? Text { get; init; }
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public int? OutputTokens { get; init; }
    public int? CachedTokens { get; init; }
    public int? InputTokens { get; init; }
    /// <summary>结束原因（openai finish_reason / anthropic stop_reason）：
    /// "length"/"max_tokens" = 输出被 max_tokens 截断，调用方需提示用户。</summary>
    public string? FinishReason { get; init; }

    /// <summary>Anthropic extended thinking 文本（thinking 启用时随响应返回；
    /// 由 Agent 写回 assistant 消息，下一轮请求原样回传，见 ProviderMessage.ThinkingText）。</summary>
    public string? ThinkingText { get; init; }

    /// <summary>Anthropic thinking 块签名（与 ThinkingText 成对）。</summary>
    public string? ThinkingSignature { get; init; }
}

/// <summary>暴露给模型的工具规范（JSON Schema 形式的参数）。</summary>
public sealed class ToolSpec
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonNode Parameters { get; init; }
}

/// <summary>Provider 调用失败（网络 / HTTP / 鉴权等）。</summary>
public sealed class ProviderException : Exception
{
    public ProviderException(string message) : base(message) { }
    public ProviderException(string message, Exception inner) : base(message, inner) { }

    /// <summary>HTTP 状态码（网络/解析错误时为 null）。</summary>
    public int? StatusCode { get; init; }

    /// <summary>是否可安全自动重试（429 / 5xx / 连接失败）。</summary>
    public bool Retryable { get; init; }
}

/// <summary>常见模型的上下文窗口近似值（token），按名称前缀最长匹配。
/// 仅用于状态栏 ctx 百分比提示（精确值以各模型文档为准，contextWindow 配置可覆盖）。</summary>
public static class KnownContextWindows
{
    private static readonly (string Prefix, int Tokens)[] Table =
    [
        ("gpt-5.1", 400_000),
        ("gpt-5", 400_000),
        ("gpt-4.1", 1_000_000),
        ("gpt-4o", 128_000),
        ("gpt-4-turbo", 128_000),
        ("o3", 200_000),
        ("o4-mini", 200_000),
        ("deepseek-reasoner", 128_000),
        ("deepseek-chat", 128_000),
        ("deepseek-v4", 128_000),
        ("deepseek-v3", 128_000),
        ("qwen3-coder", 262_144),
        ("qwen3", 131_072),
        ("qwen2.5-coder", 131_072),
        ("qwen-max", 131_072),
        ("claude-opus-5", 200_000),
        ("claude-sonnet-5", 1_000_000),
        ("claude-haiku-4-5", 200_000),
        ("claude-opus-4", 200_000),
        ("claude-sonnet-4", 200_000),
        ("claude-haiku-4", 200_000),
        ("claude-3-5", 200_000),
        ("claude-3", 200_000),
        ("gemini-2.5-pro", 1_000_000),
        ("gemini-2.5-flash", 1_000_000),
        ("gemini-2.0", 1_000_000),
        ("llama-3.1", 128_000),
        ("llama-3.3", 128_000),
        ("o1", 200_000),
        ("o3-mini", 200_000),
        ("deepseek-r1", 128_000),
        ("grok-3", 131_072),
        ("grok-4", 256_000),
        ("kimi-k2", 128_000),
        ("glm-4.5", 128_000),
        // 新前缀：glm-4.6 / gemini-3 不被上面的短前缀命中（"glm-4.6" 不以 "glm-4.5" 开头）
        ("glm-4.6", 200_000),
        ("gemini-3", 1_000_000),
        ("mistral-large", 128_000),
    ];

    /// <summary>按模型名识别上下文窗口；无法识别返回 null。
    /// 自动去掉厂商前缀（tencent/hy3）与 OpenRouter 后缀（:free）后做前缀匹配。</summary>
    public static int? TryGet(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        var name = model.ToLowerInvariant().Trim();
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..]; // 去厂商前缀
        var colon = name.IndexOf(':');
        if (colon > 0)
            name = name[..colon]; // 去 OpenRouter 变体后缀（:free 等）
        int? best = null;
        var bestLen = 0;
        foreach (var (prefix, tokens) in Table)
        {
            // 最长前缀优先：gpt-4.1-mini 命中 gpt-4.1 而非更短键
            if (name.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > bestLen)
            {
                best = tokens;
                bestLen = prefix.Length;
            }
        }
        return best;
    }
}

/// <summary>已知支持推理参数（reasoning_effort / thinking）的模型名前缀表。
/// 仅用于 auto 思考强度的兜底判断（精确能力以 /models 元数据为准）；
/// 只列「几乎肯定支持」的系列，避免对不支持的服务发错参数。无法判断返回 null（=按不支持处理）。
/// 返回按强度升序的档位列表，供 auto 取最高可用档。</summary>
public static class KnownReasoningModels
{
    /// <summary>推理系列默认支持的档位（升序），供 auto 取最高档。</summary>
    public static readonly IReadOnlyList<string> DefaultEfforts = ["low", "medium", "high"];

    private static readonly string[] ReasoningPrefixes =
    [
        "o1", "o3", "o4", "gpt-5", "deepseek-r1", "deepseek-reasoner", "claude",
    ];

    /// <summary>按模型名判断支持的推理档位；无法判断返回 null。
    /// 自动去掉厂商前缀（tencent/hy3）与 OpenRouter 后缀（:free）后做前缀匹配。</summary>
    public static IReadOnlyList<string>? TryGet(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        var name = model.ToLowerInvariant().Trim();
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..]; // 去厂商前缀
        var colon = name.IndexOf(':');
        if (colon > 0)
            name = name[..colon]; // 去 OpenRouter 变体后缀（:free 等）
        foreach (var prefix in ReasoningPrefixes)
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                // claude 系列只有 3.7 及以上支持 extended thinking：3.0/3.5/2.x 若返回档位，
                // auto 会给它们发 thinking 预算，Anthropic 直接 400
                return prefix != "claude" || ClaudeSupportsThinking(name) ? DefaultEfforts : null;
        return null;
    }

    /// <summary>claude 3.7 及以上支持 thinking；3.0/3.5（含 haiku/sonnet 变体）与 2.x 不支持。</summary>
    private static bool ClaudeSupportsThinking(string name) =>
        name.StartsWith("claude-3-7", StringComparison.Ordinal) ||
        name.StartsWith("claude-3.7", StringComparison.Ordinal) ||
        !(name.StartsWith("claude-2", StringComparison.Ordinal) ||
          name.StartsWith("claude-3", StringComparison.Ordinal));
}

/// <summary>SSE 流式解析时按 index 累积的工具调用增量（openai/anthropic 通用）。</summary>
internal sealed class StreamToolAccum
{
    public string Id = "";
    public string Name = "";
    public StringBuilder Args = new();
}

/// <summary>
/// SSE data 行组装器：规范允许一个事件的 data 跨多个 <c>data:</c> 行（按 \n 拼接后才是完整 JSON），
/// 此前逐行独立解析会把被拆开的长 JSON 当非法丢弃（文本/工具参数增量静默丢失）。
/// 宽容策略：单行即完整 JSON（或不带空行的连续事件——部分网关不发空行）立即产出；
/// 单行不完整则进缓冲，与后续行拼接，凑齐、空行或流结束时才产出。
/// </summary>
internal sealed class SseDataAssembler
{
    private readonly StringBuilder _pending = new();

    /// <summary>喂入一行原始 SSE 文本，返回本次应处理的 data 负载；无则返回 null。
    /// 空行/注释行/非 data 字段（event: 等）返回 null——event 字段由调用方先行提取。</summary>
    public string? Feed(string line)
    {
        if (line.Length == 0)
            return Flush(); // 空行 = 事件边界：冲刷未完成缓冲
        if (line.StartsWith(':'))
            return null; // SSE 注释行（如 :keepalive）
        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        var payload = line["data:".Length..].Trim();
        if (payload.Length == 0 && _pending.Length == 0)
            return null;

        var candidate = _pending.Length == 0 ? payload : _pending + "\n" + payload;
        if (LooksComplete(candidate))
        {
            _pending.Clear();
            return candidate;
        }
        _pending.Clear().Append(candidate); // 不完整：留待后续 data 行拼接
        return null;
    }

    /// <summary>流结束时冲刷缓冲：内容完整则返回，否则丢弃残缺尾部。</summary>
    public string? Flush()
    {
        if (_pending.Length == 0)
            return null;
        var s = _pending.ToString();
        _pending.Clear();
        return LooksComplete(s) ? s : null;
    }

    /// <summary>JSON 数组/对象尝试整体解析判定完整性；其他负载（如 [DONE] 哨兵）原样放行。</summary>
    private static bool LooksComplete(string s)
    {
        if (s.Length == 0)
            return false;
        if (s[0] is not ('{' or '['))
            return true;
        try
        {
            JsonNode.Parse(s);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Provider 响应字段的宽容读取：部分 OpenAI 兼容网关把 token 计数、布尔值序列化为
/// 字符串（"prompt_tokens": "123"），GetValue&lt;int&gt;() 对此直接抛异常，炸掉整次响应解析；
/// 这里统一收敛为「能解析就取值，否则返回 null/默认」。</summary>
internal static class ProviderJson
{
    /// <summary>宽容整数：数字（含整数值浮点）与数字字符串均可；其他形态或超范围返回 null。</summary>
    public static int? OptInt(JsonNode? node)
    {
        if (node is not JsonValue v)
            return null;
        if (v.TryGetValue<int>(out var i))
            return i;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)
            && d == Math.Truncate(d) && d is >= int.MinValue and <= int.MaxValue)
            return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), out var p))
            return p;
        return null;
    }

    /// <summary>宽容布尔：原生 bool 与 "true"/"false" 字符串均可；其他形态返回 null。</summary>
    public static bool? OptBool(JsonNode? node)
    {
        if (node is not JsonValue v)
            return null;
        if (v.TryGetValue<bool>(out var b))
            return b;
        if (v.TryGetValue<string>(out var s))
        {
            if (s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (s.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return null;
    }
}
