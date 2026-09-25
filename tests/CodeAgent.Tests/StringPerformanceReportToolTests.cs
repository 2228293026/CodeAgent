using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class StringPerformanceReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-strperf-" + Guid.NewGuid().ToString("N"));

    public StringPerformanceReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task StringPerformanceReport_FindsRegexInLoop()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(List<string> xs)",
            "    {",
            "        foreach (var x in xs)",
            "            var m = Regex.IsMatch(x, \"a+\");",
            "    }",
            "}");
        var result = await new StringPerformanceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("字符串性能报告: 1 个 .cs 文件", result);
        Assert.Contains("循环体内 Regex: 1", result);
        Assert.Contains("每次迭代重新解析模式", result);
    }

    [Fact]
    public async Task StringPerformanceReport_FindsConcatInLoop()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(List<string> xs)",
            "    {",
            "        var sb = \"\";",
            "        foreach (var x in xs)",
            "            sb += x;",
            "    }",
            "}");
        var result = await new StringPerformanceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("循环体内字符串拼接: 1", result);
        Assert.Contains("应改 StringBuilder", result);
    }

    [Fact]
    public async Task StringPerformanceReport_FindsNestedQuantifier()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(string s)",
            "    {",
            "        var r = new Regex(@\"(a+)+b\");",
            "    }",
            "}");
        var result = await new StringPerformanceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("疑似灾难性回溯: 1", result);
        Assert.Contains("可能灾难性回溯", result);
    }

    [Fact]
    public async Task StringPerformanceReport_RegexOutsideLoopIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(List<string> xs)",
            "    {",
            "        var n = xs.Count(Regex.IsMatch);",
            "    }",
            "}");
        var result = await new StringPerformanceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现字符串/正则性能问题", result);
    }

    [Fact]
    public async Task StringPerformanceReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new StringPerformanceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new StringPerformanceReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StringPerformanceReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
