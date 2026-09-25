using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class SourceCompositionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-comp-" + Guid.NewGuid().ToString("N"));

    public SourceCompositionReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private static (string Line, string BlockStart, string BlockEnd) Cs => ("//", "/*", "*/");

    [Theory]
    [InlineData("a.cs", "cs")]
    [InlineData("a.PY", "py")]
    [InlineData("a.ts", "ts")]
    [InlineData("a.md", null)]
    [InlineData("noext", null)]
    public void LanguageOf_RecognizesSupportedLanguages(string path, string? expected) =>
        Assert.Equal(expected, SourceCompositionReportTool.LanguageOf(path));

    [Fact]
    public void Classify_SeparatesCodeCommentBlank()
    {
        var (code, comment, blank) = SourceCompositionReportTool.Classify(
            ["", "  ", "// 注释", "var a = 1;", "int b = 2;"], Cs);
        Assert.Equal(2, code);
        Assert.Equal(1, comment);
        Assert.Equal(2, blank);
    }

    [Fact]
    public void Classify_HandlesMultilineBlockComment()
    {
        var (code, comment, blank) = SourceCompositionReportTool.Classify(
            ["/* 开始", "中间行", "结束 */", "var a = 1;"], Cs);
        Assert.Equal(1, code);
        Assert.Equal(3, comment);
        Assert.Equal(0, blank);
    }

    [Fact]
    public void Classify_CountsTrailingCodeAfterBlockComment()
    {
        // /* c */ var a = 1; —— 不能把尾部代码吞掉
        var (code, comment, _) = SourceCompositionReportTool.Classify(["/* c */ var a = 1;"], Cs);
        Assert.Equal(1, code);
        Assert.Equal(1, comment);
    }

    [Fact]
    public void Classify_HashCommentsForPython()
    {
        var (code, comment, _) = SourceCompositionReportTool.Classify(
            ["# 注释", "x = 1"], ("#", "\"\"\"", "\"\"\""));
        Assert.Equal(1, code);
        Assert.Equal(1, comment);
    }

    [Fact]
    public async Task SourceCompositionReport_AggregatesAndFlagsLowComments()
    {
        File.WriteAllText(Path.Combine(_dir, "Good.cs"), string.Join("\n",
            Enumerable.Range(0, 9).Select(i => $"// 注释{i}").Concat(["var a = 1;"])));
        File.WriteAllText(Path.Combine(_dir, "Bad.cs"), string.Join("\n",
            Enumerable.Range(0, 20).Select(i => $"var x{i} = {i};")));
        var result = await new SourceCompositionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("代码构成报告:", result);
        Assert.Contains("cs", result);
        Assert.Contains("注释占比", result);
        Assert.Contains("Bad.cs", result);
        Assert.DoesNotContain("Good.cs", result); // 注释充足，不进欠注释名单
    }

    [Fact]
    public async Task SourceCompositionReport_LanguageFilterAndBadRatio()
    {
        File.WriteAllText(Path.Combine(_dir, "a.py"), "# c\nx = 1\n");
        File.WriteAllText(Path.Combine(_dir, "b.cs"), "// c\nvar a = 1;\n");
        var py = await new SourceCompositionReportTool().ExecuteAsync(
            new JsonObject { ["languages"] = new JsonArray("py") }, Context(), CancellationToken.None);
        Assert.Contains("py", py);
        Assert.DoesNotContain("cs ", py);
        await Assert.ThrowsAsync<ToolException>(() => new SourceCompositionReportTool().ExecuteAsync(
            new JsonObject { ["low_comment_ratio"] = 500 }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task SourceCompositionReport_RejectsOutsideEmptyAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new SourceCompositionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        var empty = await new SourceCompositionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到受支持的源文件", empty);
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "// c\nvar a = 1;\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SourceCompositionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
