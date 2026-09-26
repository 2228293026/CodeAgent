using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TestQualityReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-testquality-" + Guid.NewGuid().ToString("N"));

    public TestQualityReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteTest(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    [Fact]
    public async Task TestQualityReport_FindsTautologicalAssertion()
    {
        WriteTest("SampleTests.cs", "class T", "{", "    [Fact]", "    public void A() => Assert.True(true);", "}");
        var result = await new TestQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("测试质量报告: 1 个测试文件", result);
        Assert.Contains("恒真断言: 1", result);
        Assert.Contains("与被测行为无关", result);
    }

    [Fact]
    public async Task TestQualityReport_MeaningfulAssertionIsNotFlagged()
    {
        WriteTest("SampleTests.cs", "class T", "{", "    [Fact]", "    public void A() => Assert.Equal(3, 1 + 2);", "}");
        var result = await new TestQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("恒真断言", result);
    }

    [Fact]
    public async Task TestQualityReport_FindsWeakAssertion()
    {
        WriteTest("SampleTests.cs", "class T", "{", "    [Fact]", "    public void A() => Assert.NotEmpty(Result());", "}");
        var result = await new TestQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("弱断言: 1", result);
        Assert.Contains("错误实现同样能通过", result);
    }

    [Fact]
    public async Task TestQualityReport_MultiArgAssertIsStrong()
    {
        WriteTest("SampleTests.cs", "class T", "{", "    [Fact]", "    public void A() => Assert.NotEmpty(result, \"应有内容\");", "}");
        var result = await new TestQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("弱断言", result);
    }

    [Fact]
    public async Task TestQualityReport_FindsTestFileWithoutAssertions()
    {
        WriteTest("SampleTests.cs", "class T", "{", "    [Fact]", "    public void A() { var x = 1; }", "}");
        var result = await new TestQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("缺少断言", result);
    }

    [Fact]
    public async Task TestQualityReport_IgnoresNonTestFiles()
    {
        WriteTest("A.cs", "class T", "{", "    [Fact]", "    public void A() => Assert.True(true);", "}");
        var result = await new TestQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到测试文件", result);
    }

    [Fact]
    public async Task TestQualityReport_CleanTestsHaveNoIssues()
    {
        WriteTest("SampleTests.cs", "class T", "{", "    [Fact]", "    public void A() => Assert.Equal(3, 1 + 2);", "}");
        var result = await new TestQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现测试质量问题", result);
    }

    [Fact]
    public async Task TestQualityReport_RejectsOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new TestQualityReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteTest("SampleTests.cs", "class T { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TestQualityReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
