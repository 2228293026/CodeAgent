using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CopyFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-copyfiles-" + Guid.NewGuid().ToString("N"));

    public CopyFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task CopyFiles_CopiesMappingsInOrder()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "B");
        var mappings = new JsonArray(
            new JsonObject { ["source"] = "a.txt", ["destination"] = "out/a.txt" },
            new JsonObject { ["source"] = "b.txt", ["destination"] = "out/b.txt" });

        var result = await new CopyFilesTool().ExecuteAsync(
            new JsonObject { ["mappings"] = mappings }, Context(), CancellationToken.None);

        Assert.Contains("成功 2 个", result);
        Assert.Equal("A", File.ReadAllText(Path.Combine(_dir, "out", "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(_dir, "out", "b.txt")));
    }

    [Fact]
    public async Task CopyFiles_DryRunDoesNotWrite()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "A");
        var result = await new CopyFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["mappings"] = new JsonArray(new JsonObject { ["source"] = "a.txt", ["destination"] = "copy.txt" }),
                ["dry_run"] = true,
            }, Context(), CancellationToken.None);

        Assert.Contains("dry-run", result);
        Assert.False(File.Exists(Path.Combine(_dir, "copy.txt")));
    }

    [Fact]
    public async Task CopyFiles_AllowsPerMappingOverwrite()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "new");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "old");
        var result = await new CopyFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["mappings"] = new JsonArray(new JsonObject
                {
                    ["source"] = "a.txt",
                    ["destination"] = "b.txt",
                    ["overwrite"] = true,
                }),
            }, Context(), CancellationToken.None);

        Assert.Contains("成功 1 个", result);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dir, "b.txt")));
    }

    [Fact]
    public async Task CopyFiles_ContinuesAfterMappingError()
    {
        File.WriteAllText(Path.Combine(_dir, "good.txt"), "good");
        var mappings = new JsonArray(
            new JsonObject { ["source"] = "missing.txt", ["destination"] = "missing-copy.txt" },
            new JsonObject { ["source"] = "good.txt", ["destination"] = "good-copy.txt" });
        var result = await new CopyFilesTool().ExecuteAsync(
            new JsonObject { ["mappings"] = mappings }, Context(), CancellationToken.None);

        Assert.Contains("成功 1 个", result);
        Assert.Contains("失败 1 个", result);
        Assert.True(File.Exists(Path.Combine(_dir, "good-copy.txt")));
    }

    [Fact]
    public async Task CopyFiles_StopOnErrorPropagates()
    {
        await Assert.ThrowsAsync<ToolException>(() => new CopyFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["mappings"] = new JsonArray(new JsonObject { ["source"] = "missing.txt", ["destination"] = "x.txt" }),
                ["stop_on_error"] = true,
            }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task CopyFiles_MaxFilesCapsMappings()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "B");
        var mappings = new JsonArray(
            new JsonObject { ["source"] = "a.txt", ["destination"] = "a-copy.txt" },
            new JsonObject { ["source"] = "b.txt", ["destination"] = "b-copy.txt" });
        var result = await new CopyFilesTool().ExecuteAsync(
            new JsonObject { ["mappings"] = mappings, ["max_files"] = 1 }, Context(), CancellationToken.None);

        Assert.Contains("max_files=1", result);
        Assert.True(File.Exists(Path.Combine(_dir, "a-copy.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "b-copy.txt")));
    }

    [Fact]
    public async Task CopyFiles_CanceledToken_StopsBeforeCopy()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CopyFilesTool().ExecuteAsync(
            new JsonObject { ["mappings"] = new JsonArray(new JsonObject()) }, Context(), cts.Token));
    }
}
