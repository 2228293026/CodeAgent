using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DependencyLockfileReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-lockfiles-" + Guid.NewGuid().ToString("N"));

    public DependencyLockfileReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task DependencyLockfileReport_SummarizesEcosystemsAndCounts()
    {
        File.WriteAllText(Path.Combine(_dir, "package-lock.json"), "{\"lockfileVersion\":3,\"packages\":{\"\":{},\"node_modules/a\":{},\"node_modules/b\":{}}}");
        File.WriteAllText(Path.Combine(_dir, "Cargo.lock"), "version = 3\n\n[[package]]\nname = \"alpha\"\n\n[[package]]\nname = \"beta\"\n");
        File.WriteAllText(Path.Combine(_dir, "go.sum"), "example.com/a v1.0.0 h1:abc\nexample.com/a v1.0.0/go.mod h1:def\nexample.com/b v1.1.0 h1:ghi\n");
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "package-lock.json"), "{\"packages\":{}}");
        var result = await new DependencyLockfileReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("依赖锁定报告: 3 个锁定文件", result);
        Assert.Contains("package-lock.json [npm]", result);
        Assert.Contains("依赖条目: 2", result);
        Assert.Contains("Cargo.lock [Cargo]", result);
        Assert.Contains("go.sum [Go modules]", result);
        Assert.DoesNotContain("bin/package-lock.json", result);
    }

    [Fact]
    public async Task DependencyLockfileReport_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new DependencyLockfileReportTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new DependencyLockfileReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task DependencyLockfileReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DependencyLockfileReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
