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

    /// <summary>可选响应队列：非空时按序出队（耗尽后回退 NextResponse）。多轮场景用
    /// （如第一轮返回工具调用、第二轮返回最终文本）。</summary>
    public Queue<ProviderResponse>? ResponseQueue { get; set; }

    /// <summary>为 true 时，摘要请求（单条 user 消息）返回空回复，使 LLM 摘要失败、走兜底裁剪路径。</summary>
    public bool FailSummarization { get; set; }

    /// <summary>最近一次 ChatAsync/ChatStreamAsync 收到的消息（供断言外发请求内容，如 /compact 重点是否并入指令）。</summary>
    public IReadOnlyList<ProviderMessage>? LastMessages { get; private set; }

    private ProviderResponse Take() =>
        ResponseQueue is { Count: > 0 } ? ResponseQueue.Dequeue()
        : NextResponse ?? new ProviderResponse { Text = "ok" };

    public Task<ProviderResponse> ChatAsync(
        IReadOnlyList<ProviderMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        string thinkingEffort,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); // 与真实 Provider 一致：入口即尊重取消令牌（ESC 取消测试依赖）
        LastMessages = messages;
        if (FailSummarization && messages.Count == 1 && messages[0].Role == MessageRole.User)
            return Task.FromResult(new ProviderResponse { Text = null });
        return Task.FromResult(Take());
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
        ct.ThrowIfCancellationRequested(); // 与真实 Provider 一致：入口即尊重取消令牌
        LastMessages = messages;
        var resp = Take();
        if (resp.Text is { } t && onText is not null)
            onText(t);
        return Task.FromResult(resp);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(["fake-model"]);
}
