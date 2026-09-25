using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ProjectStatsToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-stats-" + Guid.NewGuid().ToString("N"));

    public ProjectStatsToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ProjectStats_GroupsExtensionsAndSkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "src", "one.cs"), "a\nb\n");
        File.WriteAllText(Path.Combine(_dir, "src", "two.cs"), "c\n");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# title\n");
        File.WriteAllText(Path.Combine(_dir, "bin", "ignored.cs"), "ignored\n");

        var result = await new ProjectStatsTool().ExecuteAsync(
            new JsonObject { ["count_lines"] = true }, Context(), CancellationToken.None);

        Assert.Contains("文件: 3", result);
        Assert.Contains(".cs: 2 个", result);
        Assert.Contains(".md: 1 个", result);
        Assert.DoesNotContain("ignored", result);
        Assert.Contains("总行数: 4", result);
    }

    [Fact]
    public async Task ProjectStats_FiltersExtensionsAndListsLargestFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "small.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "large.cs"), new string('x', 200));
        File.WriteAllText(Path.Combine(_dir, "other.md"), new string('m', 50));

        var result = await new ProjectStatsTool().ExecuteAsync(
            new JsonObject
            {
                ["include_extensions"] = new JsonArray("cs", ".md"),
                ["top_files"] = 2,
            }, Context(), CancellationToken.None);

        Assert.Contains("文件: 2", result);
        Assert.Contains("large.cs", result);
        Assert.DoesNotContain("small.txt", result);
        Assert.Contains("最大的 2 个文件", result);
    }

    [Fact]
    public async Task ProjectStats_MaxFilesReportsCap()
    {
        for (var i = 0; i < 4; i++)
            File.WriteAllText(Path.Combine(_dir, $"file{i}.txt"), "x");

        var result = await new ProjectStatsTool().ExecuteAsync(
            new JsonObject { ["max_files"] = 2 }, Context(), CancellationToken.None);

        Assert.Contains("文件: 2", result);
        Assert.Contains("达到 max_files=2 上限", result);
    }

    [Fact]
    public async Task ProjectStats_OutsidePathIsRejected()
    {
        await Assert.ThrowsAsync<ToolException>(() => new ProjectStatsTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task ProjectStats_CanceledToken_StopsBeforeScan()
    {
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProjectStatsTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
