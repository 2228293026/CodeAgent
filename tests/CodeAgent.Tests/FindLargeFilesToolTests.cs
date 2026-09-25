using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FindLargeFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-largefiles-" + Guid.NewGuid().ToString("N"));

    public FindLargeFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FindLargeFiles_RanksFilesBySize()
    {
        File.WriteAllBytes(Path.Combine(_dir, "small.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_dir, "medium.bin"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(_dir, "large.bin"), new byte[4096]);

        var result = await new FindLargeFilesTool().ExecuteAsync(
            new JsonObject { ["min_size_kb"] = 1, ["include_extensions"] = new JsonArray("bin") },
            Context(), CancellationToken.None);
        Assert.Contains("large.bin", result);
        Assert.Contains("medium.bin", result);
        Assert.DoesNotContain("small.bin", result);
        Assert.True(result.IndexOf("large.bin", StringComparison.Ordinal) < result.IndexOf("medium.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindLargeFiles_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllBytes(Path.Combine(_dir, "root.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(_dir, "bin", "ignored.bin"), new byte[8192]);

        var result = await new FindLargeFilesTool().ExecuteAsync(
            new JsonObject { ["min_size_kb"] = 1, ["include_extensions"] = new JsonArray("bin") },
            Context(), CancellationToken.None);
        Assert.Contains("root.bin", result);
        Assert.DoesNotContain("ignored.bin", result);
    }

    [Fact]
    public async Task FindLargeFiles_ReportsScanCap()
    {
        for (var i = 0; i < 4; i++)
            File.WriteAllBytes(Path.Combine(_dir, $"file{i}.bin"), new byte[2048]);

        var result = await new FindLargeFilesTool().ExecuteAsync(
            new JsonObject { ["min_size_kb"] = 1, ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task FindLargeFiles_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FindLargeFilesTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FindLargeFiles_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindLargeFilesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
