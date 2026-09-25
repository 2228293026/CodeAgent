using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TestFlakinessReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-flaky-" + Guid.NewGuid().ToString("N"));

    public TestFlakinessReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    [Theory]
    [InlineData("tests/FooTests.cs", true)]
    [InlineData("src/Thing.Test.cs", true)]
    [InlineData("src/Program.cs", false)]
    [InlineData("README.md", false)]
    public void LooksLikeTest_DetectsTestFiles(string path, bool expected) =>
        Assert.Equal(expected, TestFlakinessReportTool.LooksLikeTest(path));

    [Fact]
    public async Task TestFlakinessReport_CountsSignalsWithAdvice()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "tests"));
        File.WriteAllText(Path.Combine(_dir, "tests", "A.cs"), string.Join("\n",
            "[Fact(Skip = \"未实现\")]",
            "public void T1() { Thread.Sleep(50); }",
            "public void T2() { await Task.Delay(10); }",
            "public void T3() { var d = DateTime.Now; }"));
        var result = await new TestFlakinessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("测试稳定性信号报告: 4 处命中", result);
        Assert.Contains("跳过测试: 1", result);
        Assert.Contains("Thread.Sleep: 1", result);
        Assert.Contains("短 Task.Delay: 1", result);
        Assert.Contains("时间依赖: 1", result);
        Assert.Contains("固定等待是慢且不稳定的根因", result);
        Assert.Contains("[跳过测试] tests/A.cs:1", result);
    }

    [Fact]
    public async Task TestFlakinessReport_SignalFilterAndBadSignal()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "tests"));
        File.WriteAllText(Path.Combine(_dir, "tests", "A.cs"), "Thread.Sleep(10);\nvar n = DateTime.Now;\n");
        var args = new JsonObject { ["signals"] = new JsonArray("Thread.Sleep") };
        var result = await new TestFlakinessReportTool().ExecuteAsync(args, Context(), CancellationToken.None);
        Assert.Contains("测试稳定性信号报告: 1 处命中", result);
        Assert.DoesNotContain("时间依赖", result);
        await Assert.ThrowsAsync<ToolException>(() => new TestFlakinessReportTool().ExecuteAsync(
            new JsonObject { ["signals"] = new JsonArray("NOPE") }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task TestFlakinessReport_IgnoresNonTestFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "Program.cs"), "Thread.Sleep(10);\n");
        var result = await new TestFlakinessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("测试稳定性信号报告: 0 处命中", result);
    }

    [Fact]
    public async Task TestFlakinessReport_RejectsOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new TestFlakinessReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TestFlakinessReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
