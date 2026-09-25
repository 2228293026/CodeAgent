using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FindUnreadableFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-unreadable-" + Guid.NewGuid().ToString("N"));

    public FindUnreadableFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FindUnreadableFiles_ReportsReadableScanWithoutErrors()
    {
        File.WriteAllText(Path.Combine(_dir, "ok.txt"), "ok");
        var result = await new FindUnreadableFilesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("不可读文件报告", result);
        Assert.Contains("0 个", result);
    }

    [Fact]
    public async Task FindUnreadableFiles_ReportsExclusivelyLockedFileOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(_dir, "locked.txt");
        File.WriteAllText(path, "locked");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await new FindUnreadableFilesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("locked.txt", result);
        Assert.Contains("1 个", result);
    }

    [Fact]
    public async Task FindUnreadableFiles_FiltersExtensionsAndSkipsIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "keep.log"), "keep");
        File.WriteAllText(Path.Combine(_dir, "skip.txt"), "skip");
        File.WriteAllText(Path.Combine(_dir, "bin", "ignored.txt"), "ignored");
        var result = await new FindUnreadableFilesTool().ExecuteAsync(
            new JsonObject { ["include_extensions"] = new JsonArray("log") }, Context(), CancellationToken.None);
        Assert.Contains("扫描 1 个", result);
        Assert.DoesNotContain("skip.txt", result);
        Assert.DoesNotContain("ignored", result);
    }

    [Fact]
    public async Task FindUnreadableFiles_ReportsScanCap()
    {
        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "x");
        var result = await new FindUnreadableFilesTool().ExecuteAsync(
            new JsonObject { ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task FindUnreadableFiles_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FindUnreadableFilesTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FindUnreadableFiles_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindUnreadableFilesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
