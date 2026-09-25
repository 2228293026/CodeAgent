using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DirectorySizeBreakdownToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-sizebreak-" + Guid.NewGuid().ToString("N"));

    public DirectorySizeBreakdownToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task DirectorySizeBreakdown_GroupsTopLevelDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src", "nested"));
        Directory.CreateDirectory(Path.Combine(_dir, "docs"));
        Directory.CreateDirectory(Path.Combine(_dir, "bin", "ignored"));
        File.WriteAllBytes(Path.Combine(_dir, "src", "nested", "a.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_dir, "docs", "b.txt"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_dir, "root.txt"), new byte[5]);
        File.WriteAllBytes(Path.Combine(_dir, "bin", "ignored", "skip.bin"), new byte[1000]);
        var result = await new DirectorySizeBreakdownTool().ExecuteAsync(
            new JsonObject { ["max_results"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("目录大小分布", result);
        Assert.Contains("src: 1 个文件", result);
        Assert.Contains("docs: 1 个文件", result);
        Assert.Contains("根目录文件 1 个", result);
        Assert.DoesNotContain("bin", result);
        Assert.Contains("未显示", result);
    }

    [Fact]
    public async Task DirectorySizeBreakdown_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new DirectorySizeBreakdownTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new DirectorySizeBreakdownTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task DirectorySizeBreakdown_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DirectorySizeBreakdownTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
