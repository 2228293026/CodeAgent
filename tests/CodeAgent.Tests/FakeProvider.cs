using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Providers;

namespace CodeAgent.Tests;

/// <summary>最小假 Provider：供不依赖真实 API 的 Agent 级测试使用。</summary>
public sealed class FakeProvider : IAgentProvider
{
    public string Name => "fake";
    public ProviderResponse? NextResponse { get; set; }

    /// <summary>为 true 时，摘要请求（单条 user 消息）返回空回复，使 LLM 摘要失败、走兜底裁剪路径。</summary>
    public bool FailSummarization { get; set; }

    public Task<ProviderResponse> ChatAsync(
        IReadOnlyList<ProviderMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        string thinkingEffort,
        CancellationToken ct)
    {
        if (FailSummarization && messages.Count == 1 && messages[0].Role == MessageRole.User)
            return Task.FromResult(new ProviderResponse { Text = null });
        return Task.FromResult(NextResponse ?? new ProviderResponse { Text = "ok" });
    }

    public Task<ProviderResponse> ChatStreamAsync(
        IReadOnlyList<ProviderMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        string thinkingEffort,
        Action<string>? onText,
        Action<string>? onReasoning,
        Action<string>? onToolFragment,
        CancellationToken ct)
    {
        var resp = NextResponse ?? new ProviderResponse { Text = "ok" };
        if (resp.Text is { } t && onText is not null)
            onText(t);
        return Task.FromResult(resp);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(["fake-model"]);
}
