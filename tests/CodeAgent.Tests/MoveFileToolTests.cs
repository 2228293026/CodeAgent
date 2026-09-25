using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class MoveFileToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-move-" + Guid.NewGuid().ToString("N"));

    public MoveFileToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task MoveFile_MovesAndCreatesParent()
    {
        File.WriteAllText(Path.Combine(_dir, "source.txt"), "content\n");
        var result = await new MoveFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "nested/moved.txt" },
            Context(), CancellationToken.None);

        Assert.Contains("已移动", result);
        Assert.False(File.Exists(Path.Combine(_dir, "source.txt")));
        Assert.Equal("content\n", File.ReadAllText(Path.Combine(_dir, "nested", "moved.txt")));
    }

    [Fact]
    public async Task MoveFile_ProtectsExistingDestination()
    {
        File.WriteAllText(Path.Combine(_dir, "source.txt"), "new");
        File.WriteAllText(Path.Combine(_dir, "destination.txt"), "old");
        var tool = new MoveFileTool();
        var context = Context();

        await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "destination.txt" },
            context, CancellationToken.None));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dir, "source.txt")));

        await tool.ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "destination.txt", ["overwrite"] = true },
            context, CancellationToken.None);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dir, "destination.txt")));
    }

    [Fact]
    public async Task MoveFile_DryRunDoesNotMove()
    {
        File.WriteAllText(Path.Combine(_dir, "source.txt"), "content");
        var result = await new MoveFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "moved.txt", ["dry_run"] = true },
            Context(), CancellationToken.None);

        Assert.Contains("[dry_run]", result);
        Assert.True(File.Exists(Path.Combine(_dir, "source.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "moved.txt")));
    }

    [Fact]
    public async Task MoveFile_UndoRestoresSourceAndRemovesNewDestination()
    {
        File.WriteAllText(Path.Combine(_dir, "source.txt"), "content");
        var context = Context();
        await new MoveFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "moved.txt" },
            context, CancellationToken.None);

        Assert.NotNull(context.Undo.TryUndo());
        Assert.Equal("content", File.ReadAllText(Path.Combine(_dir, "source.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "moved.txt")));
    }

    [Fact]
    public async Task MoveFile_UndoRestoresOverwrittenDestination()
    {
        File.WriteAllText(Path.Combine(_dir, "source.txt"), "new");
        File.WriteAllText(Path.Combine(_dir, "destination.txt"), "old");
        var context = Context();
        await new MoveFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "destination.txt", ["overwrite"] = true },
            context, CancellationToken.None);

        Assert.NotNull(context.Undo.TryUndo());
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dir, "source.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dir, "destination.txt")));
    }

    [Fact]
    public async Task MoveFile_RejectsOutsidePath()
    {
        File.WriteAllText(Path.Combine(_dir, "source.txt"), "content");
        await Assert.ThrowsAsync<ToolException>(() => new MoveFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "source.txt", ["destination"] = "../outside.txt" },
            Context(), CancellationToken.None));
    }

    [Fact]
    public async Task MoveFile_CanceledToken_StopsBeforeAccess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MoveFileTool().ExecuteAsync(
            new JsonObject { ["source"] = "missing.txt", ["destination"] = "moved.txt" },
            Context(), cts.Token));
    }
}
