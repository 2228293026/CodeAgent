using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DeferredQueryReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-defer-" + Guid.NewGuid().ToString("N"));

    public DeferredQueryReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Deferred_FindsUnmaterialisedField()
    {
        WriteSource(
            "using System.Collections.Generic;",
            "class A", "{", "    private IEnumerable<int> Items => Source.Where(v => v > 0);",
            "    private List<int> Source = new();", "}");
        var result = await new DeferredQueryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("延迟执行报告: 1 个 .cs 文件", result);
        Assert.Contains("未物化的查询", result);
        Assert.Contains("ToList", result);
    }

    [Fact]
    public async Task Deferred_FindsMutationInsideForeach()
    {
        WriteSource(
            "class A", "{", "    void M(List<int> xs)", "    {",
            "        foreach (var x in xs)", "        {", "            xs.Add(x);", "        }", "    }", "}");
        var result = await new DeferredQueryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("foreach 中修改集合", result);
        Assert.Contains("InvalidOperationException", result);
    }

    [Fact]
    public async Task Deferred_MaterialisedAndCleanCodeAreFine()
    {
        WriteSource(
            "using System.Collections.Generic;",
            "class A", "{",
            "    private readonly List<int> Items = new();",
            "    void M()", "    {", "        foreach (var x in Items) { Console.Write(x); }", "    }", "}");
        var result = await new DeferredQueryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现延迟执行陷阱", result);
    }

    [Fact]
    public async Task Deferred_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DeferredQueryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DeferredQueryReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeferredQueryReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
