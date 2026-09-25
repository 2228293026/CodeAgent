using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FindRecentFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-recent-" + Guid.NewGuid().ToString("N"));

    public FindRecentFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FindRecentFiles_ReportsRecentlyChangedFiles()
    {
        var recent = Path.Combine(_dir, "recent.txt");
        var old = Path.Combine(_dir, "old.txt");
        File.WriteAllText(recent, "new");
        File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        var result = await new FindRecentFilesTool().ExecuteAsync(
            new JsonObject { ["within_days"] = 7 }, Context(), CancellationToken.None);
        Assert.Contains("recent.txt", result);
        Assert.DoesNotContain("old.txt", result);
    }

    [Fact]
    public async Task FindRecentFiles_FiltersExtensions()
    {
        File.WriteAllText(Path.Combine(_dir, "recent.cs"), "new");
        File.WriteAllText(Path.Combine(_dir, "recent.txt"), "new");
        var result = await new FindRecentFilesTool().ExecuteAsync(
            new JsonObject { ["include_extensions"] = new JsonArray("cs") }, Context(), CancellationToken.None);
        Assert.Contains("recent.cs", result);
        Assert.DoesNotContain("recent.txt", result);
    }

    [Fact]
    public async Task FindRecentFiles_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "ignored.txt"), "new");
        File.WriteAllText(Path.Combine(_dir, "kept.txt"), "new");
        var result = await new FindRecentFilesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("kept.txt", result);
        Assert.DoesNotContain("ignored.txt", result);
    }

    [Fact]
    public async Task FindRecentFiles_ReportsScanCap()
    {
        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "new");
        var result = await new FindRecentFilesTool().ExecuteAsync(
            new JsonObject { ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task FindRecentFiles_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FindRecentFilesTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FindRecentFiles_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindRecentFilesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
