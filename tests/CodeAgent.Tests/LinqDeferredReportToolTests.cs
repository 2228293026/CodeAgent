using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LinqDeferredReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-linq-" + Guid.NewGuid().ToString("N"));

    public LinqDeferredReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Linq_FindsReusedDeferredQuery()
    {
        WriteSource(
            "using System.Collections.Generic;", "using System.Linq;", "class A", "{",
            "    void M(List<Item> items)", "    {",
            "        var q = items.Where(x => x.Active);",
            "        var a = q.Count();",
            "        foreach (var x in q) { Use(x); }", "    }",
            "    void Use(object o) { }", "}");
        var result = await new LinqDeferredReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("LINQ 延迟执行报告: 1 个 .cs 文件", result);
        Assert.Contains("延迟查询被反复使用", result);
        Assert.Contains("重跑", result);
    }

    [Fact]
    public async Task Linq_MaterializedListIsClean()
    {
        WriteSource(
            "using System.Collections.Generic;", "using System.Linq;", "class A", "{",
            "    void M(List<Item> items)", "    {",
            "        var q = items.Where(x => x.Active).ToList();",
            "        var a = q.Count;",
            "        foreach (var x in q) { Use(x); }", "    }",
            "    void Use(object o) { }", "}");
        var result = await new LinqDeferredReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("延迟查询被反复使用", result);
    }

    [Fact]
    public async Task Linq_FindsMutationAfterQuery()
    {
        WriteSource(
            "using System.Collections.Generic;", "using System.Linq;", "class A", "{",
            "    void M(List<Item> items)", "    {",
            "        var q = items.Where(x => x.Active);",
            "        items.Add(New());", "    }",
            "    Item New() => new Item();", "}");
        var result = await new LinqDeferredReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("查询后改写集合", result);
    }

    [Fact]
    public async Task Linq_QueryUsedOnceIsClean()
    {
        WriteSource(
            "using System.Collections.Generic;", "using System.Linq;", "class A", "{",
            "    void M(List<Item> items)", "    {",
            "        var q = items.Where(x => x.Active);",
            "        var a = q.Count();", "    }", "}");
        var result = await new LinqDeferredReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("延迟查询被反复使用", result);
    }

    [Fact]
    public void TheOperatorListsDoNotOverlap()
    {
        // Where 既是延迟算子也是终端写法的一部分——两组不能混淆
        Assert.Contains("Where", LinqDeferredReportTool.Deferred);
        Assert.Contains("ToList", LinqDeferredReportTool.Terminal);
        Assert.DoesNotContain("ToList", LinqDeferredReportTool.Deferred);
        Assert.DoesNotContain("Where", LinqDeferredReportTool.Terminal);
    }

    [Fact]
    public async Task Linq_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new LinqDeferredReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new LinqDeferredReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LinqDeferredReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
