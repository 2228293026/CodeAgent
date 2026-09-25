using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CompareDirectoriesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-comparedirs-" + Guid.NewGuid().ToString("N"));
    private readonly string _left = "left";
    private readonly string _right = "right";

    public CompareDirectoriesToolTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, _left));
        Directory.CreateDirectory(Path.Combine(_dir, _right));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteLeft(string relative, string text) => File.WriteAllText(Path.Combine(_dir, _left, relative), text);
    private void WriteRight(string relative, string text) => File.WriteAllText(Path.Combine(_dir, _right, relative), text);

    [Fact]
    public async Task CompareDirectories_ReportsAddedRemovedAndModified()
    {
        WriteLeft("same.txt", "same");
        WriteLeft("removed.txt", "removed");
        WriteLeft("changed.txt", "old");
        WriteRight("same.txt", "same");
        WriteRight("added.txt", "added");
        WriteRight("changed.txt", "new");

        var result = await new CompareDirectoriesTool().ExecuteAsync(
            new JsonObject { ["left"] = _left, ["right"] = _right }, Context(), CancellationToken.None);
        Assert.Contains("仅左侧", result);
        Assert.Contains("removed.txt", result);
        Assert.Contains("added.txt", result);
        Assert.Contains("修改: changed.txt", result);
        Assert.Contains("- old", result);
        Assert.Contains("+ new", result);
    }

    [Fact]
    public async Task CompareDirectories_CanIgnoreWhitespace()
    {
        WriteLeft("a.txt", "value\n");
        WriteRight("a.txt", "value  \n");
        var result = await new CompareDirectoriesTool().ExecuteAsync(
            new JsonObject { ["left"] = _left, ["right"] = _right, ["ignore_whitespace"] = true },
            Context(), CancellationToken.None);
        Assert.Contains("相同 1", result);
        Assert.DoesNotContain("修改:", result);
    }

    [Fact]
    public async Task CompareDirectories_ReportsMaxFilesCap()
    {
        WriteLeft("a.txt", "a");
        WriteRight("a.txt", "b");
        WriteLeft("b.txt", "b");
        WriteRight("b.txt", "c");
        var result = await new CompareDirectoriesTool().ExecuteAsync(
            new JsonObject { ["left"] = _left, ["right"] = _right, ["max_files"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=1", result);
    }

    [Fact]
    public async Task CompareDirectories_MissingDirectoryThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new CompareDirectoriesTool().ExecuteAsync(
            new JsonObject { ["left"] = "missing", ["right"] = _right }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task CompareDirectories_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CompareDirectoriesTool().ExecuteAsync(
            new JsonObject { ["left"] = _left, ["right"] = _right }, Context(), cts.Token));
    }
}
