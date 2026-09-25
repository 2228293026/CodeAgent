using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DirectoryDepthReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-depth-" + Guid.NewGuid().ToString("N"));

    public DirectoryDepthReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task DirectoryDepthReport_ReportsDepthAndDeepestPaths()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src", "nested", "deep"));
        Directory.CreateDirectory(Path.Combine(_dir, ".hidden"));
        Directory.CreateDirectory(Path.Combine(_dir, "bin", "ignored"));
        var result = await new DirectoryDepthReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("目录深度报告", result);
        Assert.Contains("深度 3: 1", result);
        Assert.Contains("src/nested/deep", result);
        Assert.DoesNotContain(".hidden", result);
        Assert.DoesNotContain("bin/ignored", result);
    }

    [Fact]
    public async Task DirectoryDepthReport_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new DirectoryDepthReportTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new DirectoryDepthReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task DirectoryDepthReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DirectoryDepthReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
