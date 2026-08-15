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

/// <summary>SSE 流式解析时按 index 累积的工具调用增量（openai/anthropic 通用）。</summary>
internal sealed class StreamToolAccum
{
    public string Id = "";
    public string Name = "";
    public StringBuilder Args = new();
}
