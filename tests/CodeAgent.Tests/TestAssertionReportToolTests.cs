using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TestAssertionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-assert-" + Guid.NewGuid().ToString("N"));

    public TestAssertionReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteTest(string name, params string[] lines)
    {
        var dir = Path.Combine(_dir, "tests");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), string.Join("\n", lines));
    }

    [Fact]
    public async Task TestAssertionReport_FindsTautologyAndEmptyAssert()
    {
        WriteTest("WeakTests.cs",
            "[Fact]",
            "public void T()",
            "{",
            "    Assert.True(true);",
            "    Assert.NotEmpty(result);",
            "}");
        var result = await new TestAssertionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("测试断言报告: 1 个测试文件", result);
        Assert.Contains("恒真断言: 1", result);
        Assert.Contains("等于没有测试", result);
        Assert.Contains("仅非空断言: 1", result);
        Assert.Contains("tests/WeakTests.cs:4", result);
    }

    [Fact]
    public async Task TestAssertionReport_FindsBrittleAndSkipped()
    {
        WriteTest("BrittleTests.cs",
            "[Fact(Skip = \"待实现\")]",
            "public void T()",
            "{",
            "    Assert.Contains(\"这是一段很长的中文期望输出文案\", output);",
            "}");
        var result = await new TestAssertionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("精确文案断言: 1", result);
        Assert.Contains("TODO 测试: 1", result);
        Assert.Contains("长期静默失效", result);
    }

    [Fact]
    public async Task TestAssertionReport_StrongAssertsAreNotFlagged()
    {
        WriteTest("GoodTests.cs",
            "[Fact]",
            "public void T()",
            "{",
            "    Assert.Equal(3, list.Count);",
            "    Assert.True(list.Contains(\"x\"), \"应包含 x\");",
            "}");
        var result = await new TestAssertionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("2 个断言", result);
        Assert.Contains("0 处可疑", result);
    }

    [Fact]
    public async Task TestAssertionReport_IgnoresNonTestFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "Program.cs"), "Assert.True(true);\n");
        var result = await new TestAssertionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到测试文件", result);
    }

    [Fact]
    public async Task TestAssertionReport_RejectsOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new TestAssertionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteTest("A.cs", "Assert.Equal(1, 1);");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TestAssertionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
