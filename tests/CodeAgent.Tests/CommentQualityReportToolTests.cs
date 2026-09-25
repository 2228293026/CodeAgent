using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CommentQualityReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-commentq-" + Guid.NewGuid().ToString("N"));

    public CommentQualityReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteSource(params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, "A.cs"), string.Join("\n", lines));

    [Fact]
    public async Task CommentQualityReport_FindsOwnerlessMarker()
    {
        WriteSource(
            "class A",
            "{",
            "    // TODO: 修好这个解析",
            "    //",
            "}");
        var result = await new CommentQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("注释质量报告: 1 个文件", result);
        Assert.Contains("TODO: 1", result);
        Assert.Contains("标记无责任人: 1", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task CommentQualityReport_AcceptsOwnedMarker()
    {
        WriteSource("class A", "{", "    // TODO(@zhang): 修好这个解析", "}");
        var result = await new CommentQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("TODO: 1", result);
        Assert.DoesNotContain("标记无责任人", result);
    }

    [Fact]
    public async Task CommentQualityReport_FindsEmptyCommentLine()
    {
        WriteSource("class A", "{", "    //", "}");
        var result = await new CommentQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("空注释行: 1", result);
    }

    [Fact]
    public async Task CommentQualityReport_CleanFileHasNoMarkers()
    {
        WriteSource("class A", "{", "    // 计算总和", "    int V => 42;", "}");
        var result = await new CommentQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("无 TODO/FIXME/HACK/XXX/BUG", result);
        Assert.DoesNotContain("标记无责任人", result);
        Assert.DoesNotContain("空注释行", result);
    }

    [Fact]
    public async Task CommentQualityReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new CommentQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new CommentQualityReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CommentQualityReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
