using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class PerformanceAntiPatternReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-perfanti-" + Guid.NewGuid().ToString("N"));

    public PerformanceAntiPatternReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task PerfReport_FindsNestedLoopLinearScan()
    {
        WriteSource(
            "class A",
            "{",
            "    bool M(System.Collections.Generic.List<string> xs, System.Collections.Generic.List<string> ys)",
            "    {",
            "        foreach (var x in xs)",
            "            foreach (var y in ys)",
            "                if (x.Contains(y)) return true;",
            "        return false;",
            "    }",
            "}");
        var result = await new PerformanceAntiPatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("性能反模式报告: 1 个 .cs 文件", result);
        Assert.Contains("嵌套循环线性检索", result);
        Assert.Contains("O(n²)", result);
    }

    [Fact]
    public async Task PerfReport_FindsAllocationInLoop()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(System.Collections.Generic.List<string> xs)",
            "    {",
            "        foreach (var x in xs)",
            "        {",
            "            var sb = new System.Text.StringBuilder();",
            "        }",
            "    }",
            "}");
        var result = await new PerformanceAntiPatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("循环内反复分配", result);
        Assert.Contains("应提到循环外", result);
    }

    [Fact]
    public async Task PerfReport_FindsRepeatedComputationInLoop()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(System.Collections.Generic.List<string> xs)",
            "    {",
            "        foreach (var x in xs)",
            "        {",
            "            var a = xs.Count;",
            "            var b = xs.Count;",
            "            var c = xs.Count;",
            "        }",
            "    }",
            "}");
        var result = await new PerformanceAntiPatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("循环内重复计算", result);
        Assert.Contains("xs.Count", result);
    }

    [Fact]
    public async Task PerfReport_SingleLoopIsNotReportedAsNested()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(System.Collections.Generic.List<string> xs)",
            "    {",
            "        foreach (var x in xs)",
            "            if (x.Contains(\"a\")) { }",
            "    }",
            "}");
        var result = await new PerformanceAntiPatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("嵌套循环线性检索", result);
    }

    [Fact]
    public async Task PerfReport_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    const int N = 3;", "    int V => N;", "}");
        var result = await new PerformanceAntiPatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现性能反模式", result);
    }

    [Fact]
    public async Task PerfReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new PerformanceAntiPatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new PerformanceAntiPatternReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PerformanceAntiPatternReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
