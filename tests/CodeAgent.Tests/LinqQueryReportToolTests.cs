using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LinqQueryReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-linq-" + Guid.NewGuid().ToString("N"));

    public LinqQueryReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Linq_FindsQueryInsideLoop()
    {
        WriteSource(
            "class A", "{", "    void M(int[] xs)", "    {",
            "        foreach (var x in xs)", "        {", "            var y = xs.Where(v => v > 0);",
            "        }", "    }", "}");
        var result = await new LinqQueryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("LINQ 查询报告: 1 个 .cs 文件", result);
        Assert.Contains("循环体内重复查询", result);
        Assert.Contains("提到循环外", result);
    }

    [Fact]
    public async Task Linq_NestedIfDoesNotEndLoopScope()
    {
        // 关键回归点：循环体里套一个 if，if 的 '}' 不得把外层循环弹掉
        WriteSource(
            "class A", "{", "    void M(int[] xs)", "    {",
            "        for (int i = 0; i < 3; i++)", "        {",
            "            if (i > 0)", "            {", "                Console.Write(1);", "            }",
            "            var y = xs.Count(v => v > 0);",
            "        }", "    }", "}");
        var result = await new LinqQueryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("循环体内重复查询", result);
    }

    [Fact]
    public async Task Linq_FindsDoubleEnumeration()
    {
        WriteSource(
            "class A", "{", "    int M(int[] xs)", "    {",
            "        var n = xs.Count();",
            "        var first = xs.Any();",
            "        return n + (first ? 1 : 0);", "    }", "}");
        var result = await new LinqQueryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("同一序列枚举两遍", result);
        Assert.Contains("枚举两遍", result);
    }

    [Fact]
    public async Task Linq_OutsideLoopIsNotReported()
    {
        WriteSource("class A", "{", "    int M(int[] xs) => xs.Count(v => v > 0);", "}");
        var result = await new LinqQueryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("循环体内重复查询", result);
    }

    [Fact]
    public async Task Linq_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new LinqQueryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 LINQ 查询陷阱", result);
    }

    [Fact]
    public async Task Linq_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new LinqQueryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new LinqQueryReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LinqQueryReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
