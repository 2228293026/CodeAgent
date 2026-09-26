using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class StringFormatReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-strfmt-" + Guid.NewGuid().ToString("N"));

    public StringFormatReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task StringFormat_FindsLoopConcat()
    {
        WriteSource(
            "class A",
            "{",
            "    string M(System.Collections.Generic.List<string> xs)",
            "    {",
            "        var sb = \"\";",
            "        foreach (var x in xs)",
            "        {",
            "            sb += x;",
            "        }",
            "        return sb;",
            "    }",
            "}");
        var result = await new StringFormatReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("字符串构造报告: 1 个 .cs 文件", result);
        Assert.Contains("循环内 += 拼接", result);
        Assert.Contains("StringBuilder", result);
    }

    [Fact]
    public async Task StringFormat_ConcatOutsideLoopIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    string M(string x)",
            "    {",
            "        var sb = \"\";",
            "        sb += x;",
            "        return sb;",
            "    }",
            "}");
        var result = await new StringFormatReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("循环内 += 拼接", result);
    }

    [Fact]
    public async Task StringFormat_FindsArityMismatch()
    {
        WriteSource("class A", "{", "    string M(int a, int b) => string.Format(\"{0} {1}\", a);", "}");
        var result = await new StringFormatReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("格式串参数不匹配", result);
        Assert.Contains("FormatException", result);
    }

    [Fact]
    public async Task StringFormat_MatchingArityIsFine()
    {
        WriteSource("class A", "{", "    string M(int a, int b) => string.Format(\"{0} {1}\", a, b);", "}");
        var result = await new StringFormatReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("格式串参数不匹配", result);
    }

    [Fact]
    public async Task StringFormat_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new StringFormatReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现字符串构造问题", result);
    }

    [Fact]
    public async Task StringFormat_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new StringFormatReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new StringFormatReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StringFormatReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
