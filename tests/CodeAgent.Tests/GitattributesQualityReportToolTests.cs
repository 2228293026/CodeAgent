using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitattributesQualityReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-ga-" + Guid.NewGuid().ToString("N"));

    public GitattributesQualityReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteAttributes(params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, ".gitattributes"), string.Join("\n", lines));

    [Fact]
    public async Task Gitattributes_ReportsWhenMissing()
    {
        var result = await new GitattributesQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("没有 .gitattributes", result);
    }

    [Fact]
    public async Task Gitattributes_FindsMissingTextAuto()
    {
        WriteAttributes("*.cs eol=lf");
        var result = await new GitattributesQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("gitattributes 质量报告", result);
        Assert.Contains("缺少 text=auto", result);
        Assert.DoesNotContain("缺少 eol 声明", result);
    }

    [Fact]
    public async Task Gitattributes_FindsMissingEol()
    {
        WriteAttributes("* text=auto");
        var result = await new GitattributesQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("缺少 eol 声明", result);
    }

    [Fact]
    public async Task Gitattributes_FindsDuplicateRule()
    {
        WriteAttributes("* text=auto", "*.cs eol=lf", "*.cs eol=lf");
        var result = await new GitattributesQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重复规则", result);
    }

    [Fact]
    public async Task Gitattributes_FindsBinaryMistake()
    {
        WriteAttributes("* text=auto", "*.cs eol=lf", "*.png text");
        var result = await new GitattributesQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("二进制误当文本", result);
        Assert.Contains("乱码", result);
    }

    [Fact]
    public async Task Gitattributes_CompleteFileIsClean()
    {
        WriteAttributes("# 注释", "* text=auto", "*.cs eol=lf", "*.png binary");
        var result = await new GitattributesQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 .gitattributes 质量问题", result);
    }

    [Fact]
    public async Task Gitattributes_RejectsOutsideAndCancellation()
    {
        WriteAttributes("* text=auto");
        await Assert.ThrowsAsync<ToolException>(() => new GitattributesQualityReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitattributesQualityReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
