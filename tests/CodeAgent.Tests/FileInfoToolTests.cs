using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FileInfoToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-info-" + Guid.NewGuid().ToString("N"));

    public FileInfoToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FileInfo_ReportsTextMetadataAndHash()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "alpha beta\nalpha\n");
        var result = await new FileInfoTool().ExecuteAsync(
            new JsonObject { ["path"] = "notes.txt", ["count_words"] = true, ["hash"] = true },
            Context(), CancellationToken.None);

        Assert.Contains("大小:", result);
        Assert.Contains("编码: UTF-8", result);
        Assert.Contains("行数: 2", result);
        Assert.Contains("单词数: 3", result);
        Assert.Contains("SHA256:", result);
    }

    [Fact]
    public async Task FileInfo_CanSkipTextStatistics()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "line\n");
        var result = await new FileInfoTool().ExecuteAsync(
            new JsonObject { ["path"] = "notes.txt", ["count_lines"] = false },
            Context(), CancellationToken.None);

        Assert.DoesNotContain("行数:", result);
    }

    [Fact]
    public async Task FileInfo_RejectsDirectoryAndMissingFile()
    {
        var tool = new FileInfoTool();
        await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["path"] = "." }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["path"] = "missing.txt" }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FileInfo_OutsideWorkspaceIsRejected()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FileInfoTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FileInfo_CanceledToken_StopsBeforeAccess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileInfoTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing.txt" }, Context(), cts.Token));
    }
}
