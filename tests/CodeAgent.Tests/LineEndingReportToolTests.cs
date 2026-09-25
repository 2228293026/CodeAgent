using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LineEndingReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-lineend-" + Guid.NewGuid().ToString("N"));

    public LineEndingReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task LineEndingReport_DetectsStylesBomAndBinary()
    {
        File.WriteAllBytes(Path.Combine(_dir, "lf.txt"), Encoding.UTF8.GetBytes("one\ntwo\n"));
        File.WriteAllBytes(Path.Combine(_dir, "crlf.txt"), Encoding.UTF8.GetBytes("one\r\ntwo\r\n"));
        File.WriteAllBytes(Path.Combine(_dir, "mixed.txt"), Encoding.UTF8.GetBytes("one\ntwo\r\n"));
        File.WriteAllBytes(Path.Combine(_dir, "bom.txt"), new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x', (byte)'\n' });
        File.WriteAllBytes(Path.Combine(_dir, "binary.bin"), new byte[] { 0, 1, 2, 3 });
        var result = await new LineEndingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("换行报告", result);
        Assert.Contains("LF:", result);
        Assert.Contains("CRLF:", result);
        Assert.Contains("UTF-8 BOM: 1", result);
        Assert.Contains("二进制文件: 1", result);
        Assert.Contains("混合换行风格", result);
    }

    [Fact]
    public async Task LineEndingReport_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new LineEndingReportTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new LineEndingReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task LineEndingReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LineEndingReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
