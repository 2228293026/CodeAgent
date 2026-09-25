using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FindTemporaryFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-tempfiles-" + Guid.NewGuid().ToString("N"));

    public FindTemporaryFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FindTemporaryFiles_FindsDefaultSuffixesAndSizes()
    {
        File.WriteAllText(Path.Combine(_dir, "a.bak"), "1234");
        File.WriteAllText(Path.Combine(_dir, "b.tmp"), "x");
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "keep");
        var result = await new FindTemporaryFilesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("找到 2 个", result);
        Assert.Contains("a.bak", result);
        Assert.Contains("b.tmp", result);
        Assert.Contains("4 B", result);
        Assert.DoesNotContain("keep.txt", result);
    }

    [Fact]
    public async Task FindTemporaryFiles_AcceptsCustomSuffixes()
    {
        File.WriteAllText(Path.Combine(_dir, "a.log"), "x");
        File.WriteAllText(Path.Combine(_dir, "b.bak"), "x");
        var result = await new FindTemporaryFilesTool().ExecuteAsync(
            new JsonObject { ["suffixes"] = new JsonArray("log") }, Context(), CancellationToken.None);
        Assert.Contains("a.log", result);
        Assert.DoesNotContain("b.bak", result);
    }

    [Fact]
    public async Task FindTemporaryFiles_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "ignored.bak"), "x");
        File.WriteAllText(Path.Combine(_dir, "kept.bak"), "x");
        var result = await new FindTemporaryFilesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("kept.bak", result);
        Assert.DoesNotContain("ignored.bak", result);
    }

    [Fact]
    public async Task FindTemporaryFiles_ReportsScanCap()
    {
        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.tmp"), "x");
        var result = await new FindTemporaryFilesTool().ExecuteAsync(
            new JsonObject { ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task FindTemporaryFiles_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FindTemporaryFilesTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FindTemporaryFiles_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindTemporaryFilesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
