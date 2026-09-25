using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TestIsolationReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-testiso-" + Guid.NewGuid().ToString("N"));

    public TestIsolationReportToolTests() => Directory.CreateDirectory(_dir);

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
        File.WriteAllText(Path.Combine(_dir, "T.cs"), string.Join("\n", lines));

    [Fact]
    public async Task TestIsolationReport_FindsSharedStaticState()
    {
        WriteSource(
            "class T",
            "{",
            "    private static int _counter;",
            "    [Fact]",
            "    public void M() { }",
            "}");
        var result = await new TestIsolationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("测试隔离性报告: 1 个文件", result);
        Assert.Contains("共享可变静态状态: 1", result);
        Assert.Contains("用例间会互相污染", result);
    }

    [Fact]
    public async Task TestIsolationReport_FindsNondeterminism()
    {
        WriteSource(
            "class T",
            "{",
            "    [Fact]",
            "    public void M() { var n = DateTime.Now; Assert.True(n.Year > 2000); }",
            "}");
        var result = await new TestIsolationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("非确定性依赖: 1", result);
        Assert.Contains("重跑可能失败", result);
    }

    [Fact]
    public async Task TestIsolationReport_FindsDisposableWithoutCleanup()
    {
        WriteSource(
            "class T : IDisposable",
            "{",
            "    [Fact]",
            "    public void M() { }",
            "}");
        var result = await new TestIsolationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("缺少清理: 1", result);
        Assert.Contains("临时文件会残留", result);
    }

    [Fact]
    public async Task TestIsolationReport_DisposableWithCleanupNotFlagged()
    {
        WriteSource(
            "class T : IDisposable",
            "{",
            "    [Fact]",
            "    public void M() { }",
            "    public void Dispose() { Directory.Delete(_dir, true); }",
            "}");
        var result = await new TestIsolationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("缺少清理", result);
    }

    [Fact]
    public async Task TestIsolationReport_StaticReadonlyNotFlagged()
    {
        WriteSource(
            "class T",
            "{",
            "    private static readonly int Limit = 3;",
            "    [Fact]",
            "    public void M() { Assert.Equal(3, Limit); }",
            "}");
        var result = await new TestIsolationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现测试隔离性问题", result);
    }

    [Fact]
    public async Task TestIsolationReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new TestIsolationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new TestIsolationReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class T { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TestIsolationReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
