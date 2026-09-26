using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DuplicateMethodReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-dup-" + Guid.NewGuid().ToString("N"));

    public DuplicateMethodReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Duplicate_FindsIdenticalBodies()
    {
        WriteSource(
            "class A",
            "{",
            "    void First()", "    {", "        int a = 1;", "        int b = 2;", "        int c = 3;", "    }",
            "    void Second()", "    {", "        int a = 1;", "        int b = 2;", "        int c = 3;", "    }",
            "}");
        var result = await new DuplicateMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重复实现报告: 1 个 .cs 文件", result);
        Assert.Contains("方法体逐字重复", result);
        Assert.Contains("Second", result);
    }

    [Fact]
    public async Task Duplicate_DistinctBodiesAreFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void First()", "    {", "        int a = 1;", "        int b = 2;", "    }",
            "    void Second()", "    {", "        string s = \"x\";", "        int b = 9;", "    }",
            "}");
        var result = await new DuplicateMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("方法体逐字重复", result);
    }

    [Fact]
    public async Task Duplicate_FindsRepeatedName()
    {
        WriteSource("class A", "{", "    void Same() { }", "    void Same() { }", "}");
        var result = await new DuplicateMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("方法名重复", result);
        Assert.Contains("Same", result);
    }

    [Fact]
    public async Task Duplicate_FindsCopiedCommentBlock()
    {
        WriteSource(
            "class A",
            "{",
            "    // 第一步：准备数据",
            "    // 第二步：处理数据",
            "    // 第三步：输出数据",
            "    void A() { }",
            "    // 第一步：准备数据",
            "    // 第二步：处理数据",
            "    // 第三步：输出数据",
            "}");
        var result = await new DuplicateMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重复注释块", result);
        Assert.Contains("复制粘贴", result);
    }

    [Fact]
    public async Task Duplicate_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new DuplicateMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现重复实现", result);
    }

    [Fact]
    public async Task Duplicate_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DuplicateMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DuplicateMethodReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DuplicateMethodReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
