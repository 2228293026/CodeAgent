using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CsharpKeywordReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-kw-" + Guid.NewGuid().ToString("N"));

    public CsharpKeywordReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Keyword_FindsCaseCollision()
    {
        WriteSource("class A", "{", "    public string String = \"\";", "}");
        var result = await new CsharpKeywordReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("C# 关键字报告: 1 个 .cs 文件", result);
        Assert.Contains("仅大小写不同", result);
        Assert.Contains("String", result);
    }

    [Fact]
    public async Task Keyword_FindsKeywordShapedName()
    {
        WriteSource("class A", "{", "    public int value;", "}");
        var result = await new CsharpKeywordReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("上下文关键字同形", result);
        Assert.Contains("value", result);
    }

    [Fact]
    public async Task Keyword_ReservedKeywordNameIsACompileError()
    {
        WriteSource("class A", "{", "    public int for = 1;", "}");
        var result = await new CsharpKeywordReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("保留关键字", result);
        Assert.Contains("无法编译", result);
    }

    [Fact]
    public async Task Keyword_FindsRedundantVerbatim()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        var @name = 1;", "    }", "}");
        var result = await new CsharpKeywordReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("多余的 @ 前缀", result);
        Assert.Contains("@name", result);
    }

    [Fact]
    public async Task Keyword_VerbatimOnRealKeywordIsFine()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        var @class = 1;", "    }", "}");
        var result = await new CsharpKeywordReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("多余的 @ 前缀", result);
    }

    [Fact]
    public async Task Keyword_OrdinaryCodeIsClean()
    {
        WriteSource("class Widget", "{", "    public int Count { get; set; }", "}");
        var result = await new CsharpKeywordReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 C# 关键字问题", result);
    }

    [Fact]
    public async Task Keyword_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new CsharpKeywordReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new CsharpKeywordReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CsharpKeywordReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
