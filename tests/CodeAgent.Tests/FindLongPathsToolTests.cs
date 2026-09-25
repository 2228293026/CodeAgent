using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FindLongPathsToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-longpaths-" + Guid.NewGuid().ToString("N"));

    public FindLongPathsToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FindLongPaths_FindsPathsMeetingThreshold()
    {
        File.WriteAllText(Path.Combine(_dir, "short.txt"), "x");
        var result = await new FindLongPathsTool().ExecuteAsync(
            new JsonObject { ["min_length"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("short.txt", result);
        Assert.Contains("字符", result);
    }

    [Fact]
    public async Task FindLongPaths_RespectsLargeThreshold()
    {
        File.WriteAllText(Path.Combine(_dir, "short.txt"), "x");
        var result = await new FindLongPathsTool().ExecuteAsync(
            new JsonObject { ["min_length"] = 10000 }, Context(), CancellationToken.None);
        Assert.Contains("找到 0 个", result);
    }

    [Fact]
    public async Task FindLongPaths_FiltersExtensionsAndSkipsIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "keep.log"), "x");
        File.WriteAllText(Path.Combine(_dir, "skip.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "bin", "ignored.log"), "x");
        var result = await new FindLongPathsTool().ExecuteAsync(
            new JsonObject { ["min_length"] = 1, ["include_extensions"] = new JsonArray("log") }, Context(), CancellationToken.None);
        Assert.Contains("keep.log", result);
        Assert.DoesNotContain("skip.txt", result);
        Assert.DoesNotContain("ignored.log", result);
    }

    [Fact]
    public async Task FindLongPaths_ReportsScanCap()
    {
        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "x");
        var result = await new FindLongPathsTool().ExecuteAsync(
            new JsonObject { ["min_length"] = 1, ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task FindLongPaths_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FindLongPathsTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FindLongPaths_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindLongPathsTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
