using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CodeMetricsReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-metrics-" + Guid.NewGuid().ToString("N"));

    public CodeMetricsReportToolTests() => Directory.CreateDirectory(_dir);

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
    public void Classify_CountsBlankCommentAndCode()
    {
        var (code, comment, blank) = CodeMetricsReportTool.Classify(new[]
        {
            "// 头部注释",
            "",
            "var a = 1;",
            "/* 块注释",
            "   续行 */",
            "# python 风格",
            "return a;",
        });
        Assert.Equal(2, code);
        Assert.Equal(4, comment);   // // 、/* 行、续行 */、#
        Assert.Equal(1, blank);
    }

    [Fact]
    public void Classify_BlockCommentEndingMidLineCountsTrailingCode()
    {
        var (code, comment, blank) = CodeMetricsReportTool.Classify(new[] { "/* c */ var a = 1;" });
        Assert.Equal(1, comment);
        Assert.Equal(1, code);
        Assert.Equal(0, blank);
    }

    [Fact]
    public async Task CodeMetricsReport_AggregatesByDirectoryAndRanksFiles()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.cs"), "var a = 1;\nvar b = 2;\n// note\n");
        File.WriteAllText(Path.Combine(_dir, "b.cs"), "var c = 3;\n");
        var result = await new CodeMetricsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("2 个文件，4 行", result);
        Assert.Contains("代码行: 3", result);
        Assert.Contains("注释行: 1", result);
        Assert.Contains("src: 3 行", result);
        Assert.Contains("(根目录): 1 行", result);
        Assert.Contains("代码量最大的文件:", result);
    }

    [Fact]
    public async Task CodeMetricsReport_TopFilesZero_OmitsRanking()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "var a = 1;\n");
        var result = await new CodeMetricsReportTool().ExecuteAsync(
            new JsonObject { ["top_files"] = 0 }, Context(), CancellationToken.None);
        Assert.DoesNotContain("代码量最大的文件", result);
    }

    [Fact]
    public async Task CodeMetricsReport_RejectsOutsideCancellationAndEmptyTree()
    {
        await Assert.ThrowsAsync<ToolException>(() => new CodeMetricsReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        var empty = await new CodeMetricsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现源码文件", empty);
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "var a = 1;\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodeMetricsReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
