using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ReplaceInFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-replace-" + Guid.NewGuid().ToString("N"));

    public ReplaceInFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ReplaceInFiles_ReplacesAllGlobMatches()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.cs"), "old value\nold again\n");
        File.WriteAllText(Path.Combine(_dir, "src", "b.cs"), "old value\n");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "old value\n");

        var result = await new ReplaceInFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["patterns"] = new JsonArray("**/*.cs"),
                ["old_string"] = "old",
                ["new_string"] = "new",
            }, Context(), CancellationToken.None);

        Assert.Contains("匹配 2 个文件", result);
        Assert.Contains("成功 2 个", result);
        Assert.Equal("new value\nnew again\n", File.ReadAllText(Path.Combine(_dir, "src", "a.cs")));
        Assert.Equal("new value\n", File.ReadAllText(Path.Combine(_dir, "src", "b.cs")));
        Assert.Equal("old value\n", File.ReadAllText(Path.Combine(_dir, "README.md")));
    }

    [Fact]
    public async Task ReplaceInFiles_DryRunDoesNotWrite()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "before");
        var result = await new ReplaceInFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["patterns"] = new JsonArray("*.txt"),
                ["old_string"] = "before",
                ["new_string"] = "after",
                ["dry_run"] = true,
            }, Context(), CancellationToken.None);

        Assert.Contains("dry-run", result);
        Assert.Equal("before", File.ReadAllText(Path.Combine(_dir, "a.txt")));
    }

    [Fact]
    public async Task ReplaceInFiles_ContinuesAfterUnmatchedFile()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "nothing");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "target");

        var result = await new ReplaceInFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["patterns"] = new JsonArray("*.txt"),
                ["old_string"] = "target",
                ["new_string"] = "done",
            }, Context(), CancellationToken.None);

        Assert.Contains("成功 1 个", result);
        Assert.Contains("失败 1 个", result);
        Assert.Equal("done", File.ReadAllText(Path.Combine(_dir, "b.txt")));
    }

    [Fact]
    public async Task ReplaceInFiles_StopOnErrorPropagates()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "nothing");
        await Assert.ThrowsAsync<ToolException>(() => new ReplaceInFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["patterns"] = new JsonArray("*.txt"),
                ["old_string"] = "target",
                ["new_string"] = "done",
                ["stop_on_error"] = true,
            }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceInFiles_MaxFilesCapsBatch()
    {
        for (var i = 0; i < 4; i++)
            File.WriteAllText(Path.Combine(_dir, $"file{i}.txt"), "target");

        var result = await new ReplaceInFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["patterns"] = new JsonArray("*.txt"),
                ["old_string"] = "target",
                ["new_string"] = "done",
                ["max_files"] = 2,
            }, Context(), CancellationToken.None);

        Assert.Contains("匹配 2 个文件", result);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task ReplaceInFiles_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReplaceInFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["patterns"] = new JsonArray("*.txt"),
                ["old_string"] = "a",
                ["new_string"] = "b",
            }, Context(), cts.Token));
    }
}
