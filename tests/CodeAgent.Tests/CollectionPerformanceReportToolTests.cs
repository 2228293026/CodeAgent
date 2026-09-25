using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CollectionPerformanceReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-collperf-" + Guid.NewGuid().ToString("N"));

    public CollectionPerformanceReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task CollectionPerformanceReport_FindsLeakedInternalCollection()
    {
        WriteSource(
            "class A",
            "{",
            "    private readonly List<string> _items = new();",
            "    public List<string> All() => _items;",
            "}");
        var result = await new CollectionPerformanceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("集合性能报告: 1 个 .cs 文件", result);
        Assert.Contains("泄漏内部集合: 1", result);
        Assert.Contains("调用方可改坏对象状态", result);
    }

    [Fact]
    public async Task CollectionPerformanceReport_FindsLinqInLoop()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(List<int> xs)",
            "    {",
            "        foreach (var x in xs)",
            "            var n = xs.Where(v => v > 0).Count();",
            "    }",
            "}");
        var result = await new CollectionPerformanceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("循环体内 LINQ: 1", result);
        Assert.Contains("每次迭代重复枚举", result);
    }

    [Fact]
    public async Task CollectionPerformanceReport_FindsRepeatedMaterialization()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(List<int> xs)",
            "    {",
            "        foreach (var x in xs)",
            "            var copy = xs.ToList();",
            "    }",
            "}");
        var result = await new CollectionPerformanceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("循环体内重复物化: 1", result);
        Assert.Contains("应提到循环外", result);
    }

    [Fact]
    public async Task CollectionPerformanceReport_LinqOutsideLoopIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    int M(List<int> xs)",
            "    {",
            "        var n = xs.Where(v => v > 0).Count();",
            "        return n;",
            "    }",
            "}");
        var result = await new CollectionPerformanceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现集合性能问题", result);
    }

    [Fact]
    public async Task CollectionPerformanceReport_DefensiveCopyIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    private readonly List<string> _items = new();",
            "    public List<string> All() => new(_items);",
            "}");
        var result = await new CollectionPerformanceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现集合性能问题", result);
    }

    [Fact]
    public async Task CollectionPerformanceReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new CollectionPerformanceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new CollectionPerformanceReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionPerformanceReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
