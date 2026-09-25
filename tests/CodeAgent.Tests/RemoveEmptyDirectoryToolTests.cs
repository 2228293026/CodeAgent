using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class RemoveEmptyDirectoryToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-rmempty-" + Guid.NewGuid().ToString("N"));

    public RemoveEmptyDirectoryToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task RemoveEmptyDirectory_RemovesEmptyDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        var result = await new RemoveEmptyDirectoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "empty" }, Context(), CancellationToken.None);
        Assert.Contains("已删除空目录", result);
        Assert.False(Directory.Exists(Path.Combine(_dir, "empty")));
    }

    [Fact]
    public async Task RemoveEmptyDirectory_DryRunKeepsDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        var result = await new RemoveEmptyDirectoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "empty", ["dry_run"] = true }, Context(), CancellationToken.None);
        Assert.Contains("[dry_run]", result);
        Assert.True(Directory.Exists(Path.Combine(_dir, "empty")));
    }

    [Fact]
    public async Task RemoveEmptyDirectory_RejectsNonEmptyAndFile()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "full"));
        File.WriteAllText(Path.Combine(_dir, "full", "file.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "x");
        var tool = new RemoveEmptyDirectoryTool();
        var context = Context();
        await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["path"] = "full" }, context, CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["path"] = "file.txt" }, context, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveEmptyDirectory_MissingOkAndOutside()
    {
        var result = await new RemoveEmptyDirectoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing", ["missing_ok"] = true }, Context(), CancellationToken.None);
        Assert.Contains("已跳过", result);
        await Assert.ThrowsAsync<ToolException>(() => new RemoveEmptyDirectoryTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task RemoveEmptyDirectory_CanceledToken_StopsBeforeDelete()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RemoveEmptyDirectoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "empty" }, Context(), cts.Token));
    }
}
