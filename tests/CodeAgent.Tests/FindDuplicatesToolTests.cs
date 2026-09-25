using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FindDuplicatesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-dupes-" + Guid.NewGuid().ToString("N"));

    public FindDuplicatesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FindDuplicates_ReportsGroupsAndReclaimableSpace()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "same content");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "same content");
        File.WriteAllText(Path.Combine(_dir, "c.txt"), "different");

        var result = await new FindDuplicatesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);

        Assert.Contains("重复组: 1", result);
        Assert.Contains("a.txt", result);
        Assert.Contains("b.txt", result);
        Assert.Contains("潜在可回收空间", result);
        Assert.DoesNotContain("c.txt", result);
    }

    [Fact]
    public async Task FindDuplicates_FiltersExtensionsAndMinimumSize()
    {
        File.WriteAllText(Path.Combine(_dir, "small.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "one.cs"), "same");
        File.WriteAllText(Path.Combine(_dir, "two.cs"), "same");

        var result = await new FindDuplicatesTool().ExecuteAsync(
            new JsonObject { ["include_extensions"] = new JsonArray("cs"), ["min_size"] = 4 },
            Context(), CancellationToken.None);

        Assert.Contains("one.cs", result);
        Assert.DoesNotContain("small.txt", result);
    }

    [Fact]
    public async Task FindDuplicates_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "same");
        File.WriteAllText(Path.Combine(_dir, "bin", "b.txt"), "same");

        var result = await new FindDuplicatesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("没有发现", result);
    }

    [Fact]
    public async Task FindDuplicates_MaxFilesReportsCap()
    {
        for (var i = 0; i < 4; i++)
            File.WriteAllText(Path.Combine(_dir, $"file{i}.txt"), "x");

        var result = await new FindDuplicatesTool().ExecuteAsync(
            new JsonObject { ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("达到 max_files=2 上限", result);
    }

    [Fact]
    public async Task FindDuplicates_RejectsOutsidePath()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FindDuplicatesTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FindDuplicates_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindDuplicatesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
