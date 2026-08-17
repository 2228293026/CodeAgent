using System.Text;
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
}

/// <summary>Provider 一次调用的返回值。</summary>
public sealed class ProviderResponse
{
    public string? Text { get; init; }
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? CachedTokens { get; init; }
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
        ("gpt-5", 400_000),
        ("gpt-4.1", 1_000_000),
        ("gpt-4o", 128_000),
        ("gpt-4-turbo", 128_000),
        ("o3", 200_000),
        ("o4-mini", 200_000),
        ("deepseek-reasoner", 128_000),
        ("deepseek-chat", 128_000),
        ("deepseek-v3", 128_000),
        ("qwen3-coder", 262_144),
        ("qwen3", 131_072),
        ("qwen2.5-coder", 131_072),
        ("qwen-max", 131_072),
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
        ("kimi-k2", 128_000),
        ("glm-4.5", 128_000),
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

/// <summary>SSE 流式解析时按 index 累积的工具调用增量（openai/anthropic 通用）。</summary>
internal sealed class StreamToolAccum
{
    public string Id = "";
    public string Name = "";
    public StringBuilder Args = new();
}
