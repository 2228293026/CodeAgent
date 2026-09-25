using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CopyFileToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-copy-" + Guid.NewGuid().ToString("N"));

    public CopyFileToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task CopyFile_CreatesDestinationAndCreatesParent()
    {
        File.WriteAllText(Path.Combine(_dir, "source.txt"), "hello\nworld\n");
        var result = await new CopyFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "nested/copy.txt" },
            Context(), CancellationToken.None);

        Assert.Contains("已创建新文件", result);
        Assert.Equal("hello\nworld\n", File.ReadAllText(Path.Combine(_dir, "nested", "copy.txt")));
    }

    [Fact]
    public async Task CopyFile_ProtectsExistingDestinationUnlessOverwriteRequested()
    {
        File.WriteAllText(Path.Combine(_dir, "source.txt"), "new");
        File.WriteAllText(Path.Combine(_dir, "destination.txt"), "old");
        var tool = new CopyFileTool();
        var context = Context();

        await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "destination.txt" },
            context, CancellationToken.None));

        await tool.ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "destination.txt", ["overwrite"] = true },
            context, CancellationToken.None);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dir, "destination.txt")));
    }

    [Fact]
    public async Task CopyFile_DryRunDoesNotWrite()
    {
        File.WriteAllText(Path.Combine(_dir, "source.txt"), "content");
        var result = await new CopyFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "copy.txt", ["dry_run"] = true },
            Context(), CancellationToken.None);

        Assert.Contains("[dry_run]", result);
        Assert.False(File.Exists(Path.Combine(_dir, "copy.txt")));
    }

    [Fact]
    public async Task CopyFile_UndoRestoresOverwrittenContent()
    {
        File.WriteAllText(Path.Combine(_dir, "source.txt"), "new");
        File.WriteAllText(Path.Combine(_dir, "destination.txt"), "old");
        var context = Context();
        await new CopyFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "destination.txt", ["overwrite"] = true },
            context, CancellationToken.None);

        var undo = context.Undo.TryUndo();
        Assert.NotNull(undo);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dir, "destination.txt")));
    }

    [Fact]
    public async Task CopyFile_RejectsBinarySource()
    {
        File.WriteAllBytes(Path.Combine(_dir, "source.bin"), [0, 1, 2, 0]);
        await Assert.ThrowsAsync<ToolException>(() => new CopyFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "source.bin", ["destination"] = "copy.bin" },
            Context(), CancellationToken.None));
    }

    [Fact]
    public async Task CopyFile_RejectsOutsideDestination()
    {
        File.WriteAllText(Path.Combine(_dir, "source.txt"), "content");
        await Assert.ThrowsAsync<ToolException>(() => new CopyFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "../outside.txt" },
            Context(), CancellationToken.None));
    }

    [Fact]
    public async Task CopyFile_CanceledToken_StopsBeforeAccess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CopyFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "missing.txt", ["destination"] = "copy.txt" },
            Context(), cts.Token));
    }
}
