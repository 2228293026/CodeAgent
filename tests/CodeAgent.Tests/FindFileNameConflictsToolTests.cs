using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FindFileNameConflictsToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-nameconflicts-" + Guid.NewGuid().ToString("N"));

    public FindFileNameConflictsToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FindFileNameConflicts_FindsCaseInsensitiveNamesAcrossDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a"));
        Directory.CreateDirectory(Path.Combine(_dir, "b"));
        File.WriteAllText(Path.Combine(_dir, "a", "README.md"), "a");
        File.WriteAllText(Path.Combine(_dir, "b", "readme.MD"), "b");
        var result = await new FindFileNameConflictsTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("1 组", result);
        Assert.Contains("README.md", result);
        Assert.Contains("readme.MD", result);
    }

    [Fact]
    public async Task FindFileNameConflicts_FiltersExtensions()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a"));
        Directory.CreateDirectory(Path.Combine(_dir, "b"));
        File.WriteAllText(Path.Combine(_dir, "a", "same.cs"), "a");
        File.WriteAllText(Path.Combine(_dir, "b", "SAME.CS"), "b");
        File.WriteAllText(Path.Combine(_dir, "a", "same.txt"), "a");
        File.WriteAllText(Path.Combine(_dir, "b", "SAME.TXT"), "b");
        var result = await new FindFileNameConflictsTool().ExecuteAsync(
            new JsonObject { ["include_extensions"] = new JsonArray("cs") }, Context(), CancellationToken.None);
        Assert.Contains("SAME.CS", result);
        Assert.DoesNotContain("SAME.TXT", result);
    }

    [Fact]
    public async Task FindFileNameConflicts_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "ignored.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "kept.txt"), "x");
        var result = await new FindFileNameConflictsTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("0 组", result);
    }

    [Fact]
    public async Task FindFileNameConflicts_ReportsScanCap()
    {
        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "x");
        var result = await new FindFileNameConflictsTool().ExecuteAsync(
            new JsonObject { ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task FindFileNameConflicts_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FindFileNameConflictsTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FindFileNameConflicts_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindFileNameConflictsTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
