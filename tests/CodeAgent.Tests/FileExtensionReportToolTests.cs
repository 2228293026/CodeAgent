using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FileExtensionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-extreport-" + Guid.NewGuid().ToString("N"));

    public FileExtensionReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FileExtensionReport_GroupsCountsAndBytes()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "12345");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "123");
        File.WriteAllText(Path.Combine(_dir, "c.cs"), "class X {}");
        File.WriteAllText(Path.Combine(_dir, "README"), "readme");
        var result = await new FileExtensionReportTool().ExecuteAsync(
            new JsonObject { ["max_extensions"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("文件扩展名报告", result);
        Assert.Contains(".txt: 2 个", result);
        Assert.Contains("未显示", result);
    }

    [Fact]
    public async Task FileExtensionReport_RejectsOutsideAndMissingDirectory()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FileExtensionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new FileExtensionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FileExtensionReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileExtensionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
