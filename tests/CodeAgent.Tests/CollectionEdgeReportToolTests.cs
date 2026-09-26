using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CollectionEdgeReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-coll-" + Guid.NewGuid().ToString("N"));

    public CollectionEdgeReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Coll_FindsFirstWithoutEmptyCheck()
    {
        WriteSource("class A", "{", "    string M(List<string> xs)", "    {", "        return xs.First();", "    }", "}");
        var result = await new CollectionEdgeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("集合边界报告: 1 个 .cs 文件", result);
        Assert.Contains("未判空就取首/聚合", result);
    }

    [Fact]
    public async Task Coll_CountedFirstIsClean()
    {
        WriteSource(
            "class A", "{", "    string M(List<string> xs)", "    {",
            "        if (xs.Count > 0)", "        {", "            return xs.First();", "        }", "        return null;", "    }", "}");
        var result = await new CollectionEdgeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("未判空就取首/聚合", result);
    }

    [Fact]
    public async Task Coll_FindsIndexerOnDictionary()
    {
        WriteSource(
            "using System.Collections.Generic;", "class A", "{",
            "    Dictionary<string, int> map = new();",
            "    int M(string k)", "    {", "        return map[k];", "    }", "}");
        var result = await new CollectionEdgeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("索引访问代替 TryGetValue", result);
        Assert.Contains("KeyNotFoundException", result);
    }

    [Fact]
    public async Task Coll_FindsContainsInLoop()
    {
        WriteSource(
            "using System.Collections.Generic;", "class A", "{",
            "    List<string> names = new();",
            "    bool M(List<string> seen, string x)", "    {",
            "        foreach (var s in seen)", "        {",
            "            if (names.Contains(s)) return true;", "        }", "        return false;", "    }", "}");
        var result = await new CollectionEdgeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("循环里的 List.Contains", result);
        Assert.Contains("HashSet", result);
    }

    [Fact]
    public async Task Coll_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new CollectionEdgeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new CollectionEdgeReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionEdgeReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
