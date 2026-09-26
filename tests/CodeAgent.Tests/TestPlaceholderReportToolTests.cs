using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TestPlaceholderReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-tph-" + Guid.NewGuid().ToString("N"));

    public TestPlaceholderReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Placeholder_FindsTestWithoutAssert()
    {
        WriteSource("class A", "{", "    [Fact]", "    public void Runs()", "    {", "        Console.WriteLine(1);", "    }", "}");
        var result = await new TestPlaceholderReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("测试占位报告: 1 个 .cs 文件", result);
        Assert.Contains("没有断言的测试", result);
        Assert.Contains("Runs", result);
    }

    [Fact]
    public async Task Placeholder_FindsTautology()
    {
        WriteSource("class A", "{", "    [Fact]", "    public void Tautology()", "    {", "        Assert.True(true);", "    }", "}");
        var result = await new TestPlaceholderReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("恒真断言", result);
        Assert.Contains("Tautology", result);
    }

    [Fact]
    public async Task Placeholder_FindsCommentedOutTest()
    {
        WriteSource("class A", "{", "    // [Fact]", "    public void Old()", "    {", "        Assert.True(false);", "    }", "}");
        var result = await new TestPlaceholderReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("被注释掉的测试", result);
        Assert.Contains("[Fact]", result);
    }

    [Fact]
    public async Task Placeholder_MultiTestFileAttributesEachTestCorrectly()
    {
        // 关键回归点：断言数必须按**单个测试**统计，不能把整个文件当成一个测试。
        // 早期版本按文件统计，于是"前一个测试有断言、后面这个没有"会被一起放过。
        WriteSource(
            "class A", "{",
            "    [Fact]", "    public void HasAssert()", "    {", "        Assert.Equal(1, 1);", "    }",
            "    [Fact]", "    public void NoAssert()", "    {", "        Console.WriteLine(1);", "    }",
            "}");
        var result = await new TestPlaceholderReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("NoAssert", result);
        Assert.DoesNotContain("HasAssert 没有任何断言", result);
    }

    [Fact]
    public async Task Placeholder_CleanTestsHaveNoIssues()
    {
        WriteSource("class A", "{", "    [Fact]", "    public void Ok()", "    {", "        Assert.Equal(2, 1 + 1);", "    }", "}");
        var result = await new TestPlaceholderReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现测试占位问题", result);
    }

    [Fact]
    public async Task Placeholder_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new TestPlaceholderReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new TestPlaceholderReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TestPlaceholderReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
