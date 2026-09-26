using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CollectionUsageReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-collusage-" + Guid.NewGuid().ToString("N"));

    public CollectionUsageReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task CollectionReport_FindsStringConcatInLoop()
    {
        WriteSource(
            "class A",
            "{",
            "    string M(System.Collections.Generic.List<string> xs)",
            "    {",
            "        var sb = \"\";",
            "        foreach (var x in xs)",
            "        {",
            "            sb += x;",
            "        }",
            "        return sb;",
            "    }",
            "}");
        var result = await new CollectionUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("集合使用报告: 1 个 .cs 文件", result);
        Assert.Contains("循环内 += 拼接", result);
        Assert.Contains("StringBuilder", result);
    }

    [Fact]
    public async Task CollectionReport_FindsLinearContains()
    {
        WriteSource(
            "class A",
            "{",
            "    bool M(System.Collections.Generic.List<string> items, string key)",
            "    {",
            "        return items.Contains(key);",
            "    }",
            "}");
        var result = await new CollectionUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("List.Contains 查找", result);
        Assert.Contains("HashSet", result);
    }

    [Fact]
    public async Task CollectionReport_HashSetUsageIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    readonly System.Collections.Generic.HashSet<string> seen = new();",
            "",
            "    bool M(string key) => seen.Contains(key);",
            "}");
        var result = await new CollectionUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("List.Contains 查找", result);
    }

    [Fact]
    public async Task CollectionReport_FindsRepeatedMaterialization()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(System.Collections.Generic.List<string> items)",
            "    {",
            "        var a = items.Where(x => x != null).ToList();",
            "        var b = items.Where(x => x != null).ToList();",
            "        var c = items.Where(x => x != null).ToList();",
            "    }",
            "}");
        var result = await new CollectionUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("反复物化", result);
        Assert.Contains("应缓存结果", result);
    }

    [Fact]
    public async Task CollectionReport_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new CollectionUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现集合使用问题", result);
    }

    [Fact]
    public async Task CollectionReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new CollectionUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new CollectionUsageReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionUsageReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
