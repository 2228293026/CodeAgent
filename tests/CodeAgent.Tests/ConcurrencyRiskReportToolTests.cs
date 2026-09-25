using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ConcurrencyRiskReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-conc-" + Guid.NewGuid().ToString("N"));

    public ConcurrencyRiskReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ConcurrencyRiskReport_FindsBlockingWaits()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        var v = task.Result;",
            "        task.Wait();",
            "    }",
            "}");
        var result = await new ConcurrencyRiskReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("并发风险报告: 1 个 .cs 文件", result);
        Assert.Contains("阻塞等待: 2", result);
        Assert.Contains("A.cs:5", result);
    }

    [Fact]
    public async Task ConcurrencyRiskReport_FindsThreadSleepInAsync()
    {
        WriteSource(
            "class A",
            "{",
            "    async Task M()",
            "    {",
            "        Thread.Sleep(100);",
            "    }",
            "}");
        var result = await new ConcurrencyRiskReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("async 中阻塞: 1", result);
        Assert.Contains("应改 Task.Delay", result);
    }

    [Fact]
    public async Task ConcurrencyRiskReport_FindsStaticMutableField()
    {
        WriteSource("class A", "{", "    private static int _counter;", "}");
        var result = await new ConcurrencyRiskReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("静态可变字段: 1", result);
        Assert.Contains("并发下需同步", result);
    }

    [Fact]
    public async Task ConcurrencyRiskReport_StaticReadonlyAndMethodsNotFlagged()
    {
        WriteSource(
            "class A",
            "{",
            "    private static readonly int Limit = 10;",
            "    private static int Helper() => 1;",
            "}");
        var result = await new ConcurrencyRiskReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("静态可变字段", result);
    }

    [Fact]
    public async Task ConcurrencyRiskReport_CleanCodeHasNoRisks()
    {
        WriteSource("class A", "{", "    const int N = 3;", "    int V => N;", "}");
        var result = await new ConcurrencyRiskReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现并发风险迹象", result);
    }

    [Fact]
    public async Task ConcurrencyRiskReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ConcurrencyRiskReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ConcurrencyRiskReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConcurrencyRiskReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
