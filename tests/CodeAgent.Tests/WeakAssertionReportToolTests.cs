using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class WeakAssertionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-weak-" + Guid.NewGuid().ToString("N"));

    public WeakAssertionReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Weak_FindsTestWithoutAssert()
    {
        WriteSource(
            "class SampleTests", "{", "    [Fact]", "    public void Works()", "    {", "        Helper();", "    }",
            "    void Helper() { }", "}");
        var result = await new WeakAssertionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("弱断言报告: 1 个 .cs 文件", result);
        Assert.Contains("测试里没有断言", result);
        Assert.Contains("Works()", result);
    }

    [Fact]
    public async Task Weak_FindsAlwaysTrueAssert()
    {
        WriteSource(
            "class SampleTests", "{", "    [Fact]", "    public void Works()", "    {", "        Assert.True(true);", "    }", "}");
        var result = await new WeakAssertionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("恒真断言", result);
    }

    [Fact]
    public async Task Weak_FindsSwallowedExceptionInTest()
    {
        WriteSource(
            "class SampleTests", "{", "    [Fact]", "    public void Works()", "    {",
            "        try { Helper(); }", "        catch (Exception) { }", "    }", "    void Helper() { }", "}");
        var result = await new WeakAssertionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("测试内吞掉异常", result);
        Assert.Contains("当成通过", result);
    }

    [Fact]
    public async Task Weak_RealAssertionsAreClean()
    {
        WriteSource(
            "class SampleTests", "{", "    [Fact]", "    public void Works()", "    {", "        Assert.Equal(2, 1 + 1);", "    }", "}");
        var result = await new WeakAssertionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("测试里没有断言", result);
        Assert.DoesNotContain("恒真断言", result);
    }

    [Fact]
    public async Task Weak_AssertTrueOnRealExpressionIsFine()
    {
        // 关键误报防线：Assert.True(条件) 是正常写法，只有字面量 true 才是恒真
        WriteSource(
            "class SampleTests", "{", "    [Fact]", "    public void Works()", "    {", "        Assert.True(1 + 1 == 2);", "    }", "}");
        var result = await new WeakAssertionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("恒真断言", result);
    }

    [Fact]
    public async Task Weak_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new WeakAssertionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new WeakAssertionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WeakAssertionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
