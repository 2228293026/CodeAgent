using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FindStaleFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-stale-" + Guid.NewGuid().ToString("N"));

    public FindStaleFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FindStaleFiles_ReportsOldFilesAndAge()
    {
        var old = Path.Combine(_dir, "old.txt");
        var fresh = Path.Combine(_dir, "fresh.txt");
        File.WriteAllText(old, "old");
        File.WriteAllText(fresh, "fresh");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-100));
        var result = await new FindStaleFilesTool().ExecuteAsync(
            new JsonObject { ["older_than_days"] = 30 }, Context(), CancellationToken.None);
        Assert.Contains("old.txt", result);
        Assert.DoesNotContain("fresh.txt", result);
        Assert.Contains("天", result);
    }

    [Fact]
    public async Task FindStaleFiles_FiltersExtensions()
    {
        var old = Path.Combine(_dir, "old.log");
        File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-100));
        File.WriteAllText(Path.Combine(_dir, "old.cs"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "old.cs"), DateTime.UtcNow.AddDays(-100));
        var result = await new FindStaleFilesTool().ExecuteAsync(
            new JsonObject { ["include_extensions"] = new JsonArray("log") }, Context(), CancellationToken.None);
        Assert.Contains("old.log", result);
        Assert.DoesNotContain("old.cs", result);
    }

    [Fact]
    public async Task FindStaleFiles_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        var old = Path.Combine(_dir, "bin", "old.txt");
        File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-100));
        var result = await new FindStaleFilesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("bin", result);
    }

    [Fact]
    public async Task FindStaleFiles_ReportsScanCap()
    {
        for (var i = 0; i < 3; i++)
        {
            var file = Path.Combine(_dir, $"f{i}.txt");
            File.WriteAllText(file, "old");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-100));
        }
        var result = await new FindStaleFilesTool().ExecuteAsync(
            new JsonObject { ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task FindStaleFiles_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FindStaleFilesTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FindStaleFiles_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindStaleFilesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
