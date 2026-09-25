using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TestInventoryReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-testinventory-" + Guid.NewGuid().ToString("N"));

    public TestInventoryReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    [Fact]
    public async Task TestInventoryReport_DetectsFrameworksAndSkipsIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "tests"));
        Directory.CreateDirectory(Path.Combine(_dir, "spec"));
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "tests", "SampleTests.cs"), "using Xunit; class SampleTests { [Fact] void A() { Assert.True(true); } }");
        File.WriteAllText(Path.Combine(_dir, "tests", "test_sample.py"), "def test_sample():\n    assert True\n");
        File.WriteAllText(Path.Combine(_dir, "spec", "sample.spec.js"), "describe('sample', () => { it('works', () => { expect(1).toBe(1); }); });");
        File.WriteAllText(Path.Combine(_dir, "bin", "IgnoredTests.cs"), "using Xunit; [Fact] void X() {}");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "not a test");
        var result = await new TestInventoryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("测试清单报告", result);
        Assert.Contains("xUnit", result);
        Assert.Contains("pytest", result);
        Assert.Contains("Node/Jest", result);
        Assert.Contains("SampleTests.cs", result);
        Assert.DoesNotContain("IgnoredTests.cs", result);
        Assert.DoesNotContain("README.md [", result);
    }

    [Fact]
    public async Task TestInventoryReport_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new TestInventoryReportTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new TestInventoryReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task TestInventoryReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TestInventoryReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
