using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ChecksumManifestToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-checksums-" + Guid.NewGuid().ToString("N"));

    public ChecksumManifestToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ChecksumManifest_OutputsKnownSha256AndPath()
    {
        File.WriteAllText(Path.Combine(_dir, "hello.txt"), "hello");
        var result = await new ChecksumManifestTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("2cf24dba5fb0a30e26e83b2ac5b9e29e", result);
        Assert.Contains("hello.txt", result);
    }

    [Fact]
    public async Task ChecksumManifest_FiltersExtensionsAndShowsSize()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_dir, "b.bin"), "b");
        var result = await new ChecksumManifestTool().ExecuteAsync(
            new JsonObject { ["include_extensions"] = new JsonArray("txt"), ["include_size"] = true },
            Context(), CancellationToken.None);
        Assert.Contains("a.txt", result);
        Assert.DoesNotContain("b.bin", result);
        Assert.Contains(" 1", result);
    }

    [Fact]
    public async Task ChecksumManifest_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "root.txt"), "root");
        File.WriteAllText(Path.Combine(_dir, "bin", "ignored.txt"), "ignored");
        var result = await new ChecksumManifestTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("root.txt", result);
        Assert.DoesNotContain("ignored.txt", result);
    }

    [Fact]
    public async Task ChecksumManifest_ReportsScanCap()
    {
        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "x");
        var result = await new ChecksumManifestTool().ExecuteAsync(
            new JsonObject { ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task ChecksumManifest_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new ChecksumManifestTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task ChecksumManifest_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ChecksumManifestTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
