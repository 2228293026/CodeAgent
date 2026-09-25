using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DeleteFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-deletefiles-" + Guid.NewGuid().ToString("N"));

    public DeleteFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task DeleteFiles_DeletesPathsAndCanUndo()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "B");
        var context = Context();
        var result = await new DeleteFilesTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt", "b.txt") }, context, CancellationToken.None);
        Assert.Contains("已处理 2 个", result);
        Assert.False(File.Exists(Path.Combine(_dir, "a.txt")));
        Assert.NotNull(context.Undo.TryUndo());
        Assert.NotNull(context.Undo.TryUndo());
        Assert.True(File.Exists(Path.Combine(_dir, "a.txt")));
    }

    [Fact]
    public async Task DeleteFiles_DryRunKeepsFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "A");
        var result = await new DeleteFilesTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt"), ["dry_run"] = true }, Context(), CancellationToken.None);
        Assert.Contains("dry-run", result);
        Assert.True(File.Exists(Path.Combine(_dir, "a.txt")));
    }

    [Fact]
    public async Task DeleteFiles_MissingOkAndBackup()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "A");
        var result = await new DeleteFilesTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt", "missing.txt"), ["missing_ok"] = true, ["backup"] = true },
            Context(), CancellationToken.None);
        Assert.Contains("跳过 1 个", result);
        Assert.Equal("A", File.ReadAllText(Path.Combine(_dir, "a.txt.bak")));
    }

    [Fact]
    public async Task DeleteFiles_ContinuesAfterError()
    {
        File.WriteAllText(Path.Combine(_dir, "good.txt"), "good");
        var result = await new DeleteFilesTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("missing.txt", "good.txt") }, Context(), CancellationToken.None);
        Assert.Contains("已处理 2 个", result);
        Assert.Contains("失败 1 个", result);
        Assert.False(File.Exists(Path.Combine(_dir, "good.txt")));
    }

    [Fact]
    public async Task DeleteFiles_MaxFilesCapsPaths()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "B");
        var result = await new DeleteFilesTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt", "b.txt"), ["max_files"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=1", result);
        Assert.False(File.Exists(Path.Combine(_dir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "b.txt")));
    }

    [Fact]
    public async Task DeleteFiles_CanceledToken_StopsBeforeDelete()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeleteFilesTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt") }, Context(), cts.Token));
    }
}
