using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DeleteFileToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-delete-" + Guid.NewGuid().ToString("N"));

    public DeleteFileToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task DeleteFile_DeletesTextFileAndCanUndo()
    {
        var path = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(path, "keep me");
        var context = Context();
        var result = await new DeleteFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "notes.txt" }, context, CancellationToken.None);

        Assert.Contains("已删除", result);
        Assert.False(File.Exists(path));
        Assert.NotNull(context.Undo.TryUndo());
        Assert.Equal("keep me", File.ReadAllText(path));
    }

    [Fact]
    public async Task DeleteFile_CanCreateBackup()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "content");
        var result = await new DeleteFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "notes.txt", ["backup"] = true }, Context(), CancellationToken.None);

        Assert.Contains("备份", result);
        Assert.Equal("content", File.ReadAllText(Path.Combine(_dir, "notes.txt.bak")));
    }

    [Fact]
    public async Task DeleteFile_DryRunKeepsFile()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "content");
        var result = await new DeleteFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "notes.txt", ["dry_run"] = true }, Context(), CancellationToken.None);

        Assert.Contains("[dry_run]", result);
        Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));
    }

    [Fact]
    public async Task DeleteFile_MissingOkReturnsFriendlyMessage()
    {
        var result = await new DeleteFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing.txt", ["missing_ok"] = true }, Context(), CancellationToken.None);
        Assert.Contains("已跳过", result);
    }

    [Fact]
    public async Task DeleteFile_RejectsDirectoryAndOutsidePath()
    {
        var tool = new DeleteFileTool();
        await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["path"] = "." }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "content");
        await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["path"] = "../outside.txt" }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task DeleteFile_CanceledToken_StopsBeforeAccess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeleteFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing.txt" }, Context(), cts.Token));
    }
}
