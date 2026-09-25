using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitignorePatternReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitignore-" + Guid.NewGuid().ToString("N"));

    public GitignorePatternReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitignorePatternReport_ClassifiesRules()
    {
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "# comment\nbin/\n*.log\n/root-only\n!important.log\n");
        Directory.CreateDirectory(Path.Combine(_dir, "nested"));
        File.WriteAllText(Path.Combine(_dir, "nested", ".gitignore"), "cache/\n");
        var result = await new GitignorePatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Gitignore 模式报告: 2 个文件", result);
        Assert.Contains("bin [忽略,目录]", result);
        Assert.Contains("*.log [忽略,通配]", result);
        Assert.Contains("root-only [忽略,锚定]", result);
        Assert.Contains("important.log [否定]", result);
        Assert.Contains("总模式数: 5", result);
    }

    [Fact]
    public async Task GitignorePatternReport_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitignorePatternReportTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new GitignorePatternReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task GitignorePatternReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitignorePatternReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
