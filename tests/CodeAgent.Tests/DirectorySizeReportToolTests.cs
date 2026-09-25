using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DirectorySizeReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-dirsizes-" + Guid.NewGuid().ToString("N"));

    public DirectorySizeReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task DirectorySizeReport_AggregatesByDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.txt"), "12345");
        File.WriteAllText(Path.Combine(_dir, "src", "b.txt"), "67890");
        File.WriteAllText(Path.Combine(_dir, "root.txt"), "x");
        var result = await new DirectorySizeReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("目录空间报告", result);
        Assert.Contains("src", result);
        Assert.Contains("10 B", result);
        Assert.Contains("2 个文件", result);
    }

    [Fact]
    public async Task DirectorySizeReport_FiltersExtensions()
    {
        File.WriteAllText(Path.Combine(_dir, "keep.log"), "12345");
        File.WriteAllText(Path.Combine(_dir, "skip.txt"), "67890");
        var result = await new DirectorySizeReportTool().ExecuteAsync(
            new JsonObject { ["include_extensions"] = new JsonArray("log") }, Context(), CancellationToken.None);
        Assert.Contains("总计 5 B", result);
        Assert.DoesNotContain("skip.txt", result);
    }

    [Fact]
    public async Task DirectorySizeReport_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "ignored.dll"), new string('x', 100));
        File.WriteAllText(Path.Combine(_dir, "kept.txt"), "x");
        var result = await new DirectorySizeReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("总计 1 B", result);
        Assert.DoesNotContain("bin", result);
        Assert.DoesNotContain("ignored", result);
    }

    [Fact]
    public async Task DirectorySizeReport_ReportsScanCap()
    {
        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "x");
        var result = await new DirectorySizeReportTool().ExecuteAsync(
            new JsonObject { ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task DirectorySizeReport_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new DirectorySizeReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task DirectorySizeReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DirectorySizeReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
