using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class EncodingReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-encodingreport-" + Guid.NewGuid().ToString("N"));

    public EncodingReportToolTests()
    {
        Directory.CreateDirectory(_dir);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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
    public async Task EncodingReport_GroupsUtf8BomAndLegacyFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "plain.txt"), "plain", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_dir, "bom.txt"), "bom", new UTF8Encoding(true));
        File.WriteAllBytes(Path.Combine(_dir, "legacy.txt"), Encoding.GetEncoding("GB18030").GetBytes("中文"));

        var result = await new EncodingReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("UTF-8（无 BOM）", result);
        Assert.Contains("UTF-8 BOM", result);
        Assert.Contains("GB18030/GBK", result);
    }

    [Fact]
    public async Task EncodingReport_FiltersExtensions()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_dir, "b.cs"), "b");
        var result = await new EncodingReportTool().ExecuteAsync(
            new JsonObject { ["include_extensions"] = new JsonArray("txt") }, Context(), CancellationToken.None);
        Assert.Contains("a.txt", result);
        Assert.DoesNotContain("b.cs", result);
    }

    [Fact]
    public async Task EncodingReport_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "root.txt"), "root");
        File.WriteAllText(Path.Combine(_dir, "bin", "ignored.txt"), "ignored");
        var result = await new EncodingReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("root.txt", result);
        Assert.DoesNotContain("ignored.txt", result);
    }

    [Fact]
    public async Task EncodingReport_ReportsScanCap()
    {
        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "x");
        var result = await new EncodingReportTool().ExecuteAsync(
            new JsonObject { ["max_files"] = 2 }, Context(), CancellationToken.None);
        Assert.Contains("max_files=2", result);
    }

    [Fact]
    public async Task EncodingReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EncodingReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
