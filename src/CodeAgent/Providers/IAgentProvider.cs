namespace CodeAgent.Providers;

/// <summary>
/// 大模型 Provider 抽象。每个实现负责把统一的 ProviderMessage 序列转换为
/// 自家 API 的消息格式，并解析返回的文本与工具调用。
/// </summary>
public interface IAgentProvider
{
    /// <summary>Provider 名称（openai / anthropic 等），用于日志与错误提示。</summary>
    string Name { get; }

    /// <summary>发起一次对话请求（非流式），返回模型回复（文本 + 工具调用）。</summary>
    Task<ProviderResponse> ChatAsync(
        IReadOnlyList<ProviderMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        string thinkingEffort,
        CancellationToken ct);

    /// <summary>
    /// 发起一次对话请求（SSE 流式）。文本增量通过 onText 回调实时输出，
    /// 返回完整回复（含流式过程中组装完成的工具调用）。
    /// </summary>
    Task<ProviderResponse> ChatStreamAsync(
        IReadOnlyList<ProviderMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        string thinkingEffort,
        Action<string>? onText,
        CancellationToken ct);

    /// <summary>列出当前 Provider 可用的模型 ID（--models / /models 用）。</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}
