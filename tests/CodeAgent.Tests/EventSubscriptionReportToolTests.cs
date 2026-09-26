using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class EventSubscriptionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-events-" + Guid.NewGuid().ToString("N"));

    public EventSubscriptionReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteSource(params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, "A.cs"), string.Join("\n", lines));

    [Fact]
    public async Task EventReport_FindsMissingUnsubscribe()
    {
        WriteSource("class A", "{", "    void M(Source s) => s.Changed += OnChanged;", "}");
        var result = await new EventSubscriptionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("事件订阅报告: 1 个 .cs 文件", result);
        Assert.Contains("缺少解绑", result);
        Assert.Contains("s.Changed", result);
    }

    [Fact]
    public async Task EventReport_SubscribeAndUnsubscribeIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(Source s)",
            "    {",
            "        s.Changed += OnChanged;",
            "        s.Changed -= OnChanged;",
            "    }",
            "}");
        var result = await new EventSubscriptionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("缺少解绑", result);
    }

    [Fact]
    public async Task EventReport_FindsLambdaSubscription()
    {
        WriteSource("class A", "{", "    void M(Source s) => s.Changed += _ => Do();", "}");
        var result = await new EventSubscriptionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("lambda 订阅", result);
        Assert.Contains("无法用 -= 解绑", result);
    }

    [Fact]
    public async Task EventReport_FindsStaticEvent()
    {
        WriteSource("class A", "{", "    public static event EventHandler? Fired;", "}");
        var result = await new EventSubscriptionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("静态事件", result);
        Assert.Contains("无法回收", result);
    }

    [Fact]
    public async Task EventReport_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new EventSubscriptionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现事件订阅问题", result);
    }

    [Fact]
    public async Task EventReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new EventSubscriptionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new EventSubscriptionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EventSubscriptionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
