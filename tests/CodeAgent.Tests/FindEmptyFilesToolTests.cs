using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FindEmptyFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-emptyfiles-" + Guid.NewGuid().ToString("N"));

    public FindEmptyFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FindEmptyFiles_FindsOnlyZeroByteFiles()
    {
        File.WriteAllBytes(Path.Combine(_dir, "empty.txt"), Array.Empty<byte>());
        File.WriteAllText(Path.Combine(_dir, "full.txt"), "content");
        var result = await new FindEmptyFilesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("找到 1 个", result);
        Assert.Contains("empty.txt", result);
        Assert.DoesNotContain("full.txt", result);
    }

    [Fact]
    public async Task FindEmptyFiles_FiltersExtensions()
    {
        File.WriteAllBytes(Path.Combine(_dir, "empty.log"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(_dir, "empty.cs"), Array.Empty<byte>());
        var result = await new FindEmptyFilesTool().ExecuteAsync(
            new JsonObject { ["include_extensions"] = new JsonArray("log") }, Context(), CancellationToken.None);
        Assert.Contains("empty.log", result);
        Assert.DoesNotContain("empty.cs", result);
    }

    [Fact]
    public async Task FindEmptyFiles_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllBytes(Path.Combine(_dir, "bin", "ignored.txt"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(_dir, "kept.txt"), Array.Empty<byte>());
        var result = await new FindEmptyFilesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("kept.txt", result);
        Assert.DoesNotContain("ignored.txt", result);
    }

    [Fact]
    public async Task FindEmptyFiles_ReportsScanCap()
    {
        for (var i = 0; i < 3; i++)
            File.WriteAllBytes(Path.Combine(_dir, $"e{i}.txt"), Array.Empty<byte>());
        var result = await new FindEmptyFilesTool().ExecuteAsync(
            new JsonObject { ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task FindEmptyFiles_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FindEmptyFilesTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FindEmptyFiles_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindEmptyFilesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
