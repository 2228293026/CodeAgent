using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FindEmptyDirectoriesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-emptydirs-" + Guid.NewGuid().ToString("N"));

    public FindEmptyDirectoriesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FindEmptyDirectories_ReportsEmptyDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        Directory.CreateDirectory(Path.Combine(_dir, "full"));
        File.WriteAllText(Path.Combine(_dir, "full", "file.txt"), "x");

        var result = await new FindEmptyDirectoriesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("empty", result);
        Assert.DoesNotContain("full", result);
    }

    [Fact]
    public async Task FindEmptyDirectories_CanIncludeHidden()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".hidden-empty"));
        var normal = await new FindEmptyDirectoriesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain(".hidden-empty", normal);

        var included = await new FindEmptyDirectoriesTool().ExecuteAsync(
            new JsonObject { ["include_hidden"] = true }, Context(), CancellationToken.None);
        Assert.Contains(".hidden-empty", included);
    }

    [Fact]
    public async Task FindEmptyDirectories_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin", "empty"));
        var result = await new FindEmptyDirectoriesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("bin", result);
    }

    [Fact]
    public async Task FindEmptyDirectories_RespectsDepthAndResultCaps()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty-one"));
        Directory.CreateDirectory(Path.Combine(_dir, "empty-two"));
        var result = await new FindEmptyDirectoriesTool().ExecuteAsync(
            new JsonObject { ["max_depth"] = 1, ["max_results"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("max_results=1", result);
    }

    [Fact]
    public async Task FindEmptyDirectories_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FindEmptyDirectoriesTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FindEmptyDirectories_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindEmptyDirectoriesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
