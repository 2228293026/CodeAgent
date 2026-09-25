using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class MoveFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-movefiles-" + Guid.NewGuid().ToString("N"));

    public MoveFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task MoveFiles_MovesMappingsAndCanUndo()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "B");
        var context = Context();
        var result = await new MoveFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["mappings"] = new JsonArray(
                    new JsonObject { ["source"] = "a.txt", ["destination"] = "out/a.txt" },
                    new JsonObject { ["source"] = "b.txt", ["destination"] = "out/b.txt" }),
            }, context, CancellationToken.None);

        Assert.Contains("成功 2 个", result);
        Assert.False(File.Exists(Path.Combine(_dir, "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(_dir, "out", "b.txt")));
        Assert.NotNull(context.Undo.TryUndo());
        Assert.NotNull(context.Undo.TryUndo());
        Assert.True(File.Exists(Path.Combine(_dir, "a.txt")));
    }

    [Fact]
    public async Task MoveFiles_DryRunDoesNotWrite()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "A");
        var result = await new MoveFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["mappings"] = new JsonArray(new JsonObject { ["source"] = "a.txt", ["destination"] = "b.txt" }),
                ["dry_run"] = true,
            }, Context(), CancellationToken.None);
        Assert.Contains("dry-run", result);
        Assert.True(File.Exists(Path.Combine(_dir, "a.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "b.txt")));
    }

    [Fact]
    public async Task MoveFiles_ContinuesAfterMappingError()
    {
        File.WriteAllText(Path.Combine(_dir, "good.txt"), "good");
        var result = await new MoveFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["mappings"] = new JsonArray(
                    new JsonObject { ["source"] = "missing.txt", ["destination"] = "missing-moved.txt" },
                    new JsonObject { ["source"] = "good.txt", ["destination"] = "good-moved.txt" }),
            }, Context(), CancellationToken.None);
        Assert.Contains("成功 1 个", result);
        Assert.Contains("失败 1 个", result);
        Assert.True(File.Exists(Path.Combine(_dir, "good-moved.txt")));
    }

    [Fact]
    public async Task MoveFiles_StopOnErrorPropagates()
    {
        await Assert.ThrowsAsync<ToolException>(() => new MoveFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["mappings"] = new JsonArray(new JsonObject { ["source"] = "missing.txt", ["destination"] = "x.txt" }),
                ["stop_on_error"] = true,
            }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task MoveFiles_MaxFilesCapsMappings()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "B");
        var result = await new MoveFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["mappings"] = new JsonArray(
                    new JsonObject { ["source"] = "a.txt", ["destination"] = "a-moved.txt" },
                    new JsonObject { ["source"] = "b.txt", ["destination"] = "b-moved.txt" }),
                ["max_files"] = 1,
            }, Context(), CancellationToken.None);
        Assert.Contains("max_files=1", result);
        Assert.True(File.Exists(Path.Combine(_dir, "a-moved.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "b.txt")));
    }

    [Fact]
    public async Task MoveFiles_CanceledToken_StopsBeforeMove()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MoveFilesTool().ExecuteAsync(
            new JsonObject { ["mappings"] = new JsonArray(new JsonObject()) }, Context(), cts.Token));
    }
}
