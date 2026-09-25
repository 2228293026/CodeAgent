using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FileInfosToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-fileinfos-" + Guid.NewGuid().ToString("N"));

    public FileInfosToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FileInfos_ReportsMetadataForMultipleFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "one\ntwo");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "three");
        var result = await new FileInfosTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt", "b.txt") }, Context(), CancellationToken.None);
        Assert.Contains("a.txt", result);
        Assert.Contains("b.txt", result);
        Assert.Contains("行数", result);
    }

    [Fact]
    public async Task FileInfos_CanIncludeHashAndWords()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "one two");
        var result = await new FileInfosTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt"), ["hash"] = true, ["count_words"] = true },
            Context(), CancellationToken.None);
        Assert.Contains("SHA256:", result);
        Assert.Contains("单词数:", result);
    }

    [Fact]
    public async Task FileInfos_MissingOkSkipsMissing()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "one");
        var result = await new FileInfosTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt", "missing.txt"), ["missing_ok"] = true },
            Context(), CancellationToken.None);
        Assert.Contains("跳过 1 个", result);
    }

    [Fact]
    public async Task FileInfos_ContinuesAfterError()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "one");
        var result = await new FileInfosTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("missing.txt", "a.txt") }, Context(), CancellationToken.None);
        Assert.Contains("失败 1 个", result);
        Assert.Contains("a.txt", result);
    }

    [Fact]
    public async Task FileInfos_MaxFilesCapsPaths()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "b");
        var result = await new FileInfosTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt", "b.txt"), ["max_files"] = 1 },
            Context(), CancellationToken.None);
        Assert.Contains("max_files=1", result);
        Assert.Contains("a.txt", result);
        Assert.DoesNotContain("b.txt", result);
    }

    [Fact]
    public async Task FileInfos_CanceledToken_StopsBeforeRead()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileInfosTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("missing.txt") }, Context(), cts.Token));
    }
}
