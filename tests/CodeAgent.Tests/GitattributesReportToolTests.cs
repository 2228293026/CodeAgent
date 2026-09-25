using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitattributesReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitattributes-" + Guid.NewGuid().ToString("N"));

    public GitattributesReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitattributesReport_ClassifiesRules()
    {
        File.WriteAllText(Path.Combine(_dir, ".gitattributes"), "# comment\n*.cs text eol=lf\n*.bin binary\n*.md diff=markdown export-ignore\n");
        Directory.CreateDirectory(Path.Combine(_dir, "nested"));
        File.WriteAllText(Path.Combine(_dir, "nested", ".gitattributes"), "*.txt filter=lfs\n");
        var result = await new GitattributesReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Gitattributes 报告: 2 个文件", result);
        Assert.Contains("*.cs text eol=lf [text, eol=lf]", result);
        Assert.Contains("*.bin binary [binary]", result);
        Assert.Contains("diff=markdown", result);
        Assert.Contains("filter=lfs", result);
        Assert.Contains("总规则数: 4", result);
    }

    [Fact]
    public async Task GitattributesReport_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitattributesReportTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new GitattributesReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task GitattributesReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitattributesReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
