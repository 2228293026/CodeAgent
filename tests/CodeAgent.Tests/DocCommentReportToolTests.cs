using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DocCommentReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-doc-" + Guid.NewGuid().ToString("N"));

    public DocCommentReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Doc_FindsMissingSummary()
    {
        WriteSource("class A", "{", "    public int Value => 3;", "}");
        var result = await new DocCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("文档注释报告: 1 个 .cs 文件", result);
        Assert.Contains("公开成员缺 summary", result);
        Assert.Contains("Value", result);
    }

    [Fact]
    public async Task Doc_DocumentedPublicMemberIsFine()
    {
        WriteSource("class A", "{", "    /// <summary>值</summary>", "    public int Value => 3;", "}");
        var result = await new DocCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("公开成员缺 summary", result);
    }

    [Fact]
    public async Task Doc_FindsEmptySummary()
    {
        WriteSource("class A", "{", "    /// <summary></summary>", "    public int Value => 3;", "}");
        var result = await new DocCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("summary 为空", result);
    }

    [Fact]
    public async Task Doc_PrivateMemberNeedsNoSummary()
    {
        WriteSource("class A", "{", "    internal int Value => 3;", "}");
        var result = await new DocCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("公开成员缺 summary", result);
    }

    [Fact]
    public async Task Doc_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new DocCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现文档注释问题", result);
    }

    [Fact]
    public async Task Doc_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DocCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DocCommentReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DocCommentReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
