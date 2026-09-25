using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CodeStyleReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-style-" + Guid.NewGuid().ToString("N"));

    public CodeStyleReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteSource(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    [Fact]
    public async Task CodeStyleReport_CountsVarAndExplicitDeclarations()
    {
        WriteSource("A.cs",
            "// 文件头",
            "class A",
            "{",
            "    void M()",
            "    {",
            "        var x = 1;",
            "        var y = 2;",
            "        private int z = 3;",
            "    }",
            "}");
        var result = await new CodeStyleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("代码风格报告: 1 个 .cs 文件", result);
        Assert.Contains("var 2", result);
        Assert.Contains("显式 1", result);
        Assert.Contains("var 占比", result);
    }

    [Fact]
    public async Task CodeStyleReport_FlagsMixedBraceStyles()
    {
        WriteSource("A.cs",
            "class A",
            "{",
            "    void Kr() { var a = 1; }",
            "    void Allman()",
            "    {",
            "        var b = 2;",
            "    }",
            "}");
        var result = await new CodeStyleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("两种大括号风格混用", result);
        Assert.Contains("便于扫读", result);
    }

    [Fact]
    public async Task CodeStyleReport_FlagsMissingFileHeader()
    {
        WriteSource("A.cs", "class A { }");
        var result = await new CodeStyleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("缺文件头注释: 1 个", result);
        Assert.Contains("A.cs（无文件头注释）", result);
    }

    [Fact]
    public async Task CodeStyleReport_ConsistentStyleHasNoMixWarning()
    {
        WriteSource("A.cs",
            "// 文件头",
            "class A",
            "{",
            "    void M()",
            "    {",
            "        var x = 1;",
            "    }",
            "}");
        var result = await new CodeStyleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("混用", result);
        Assert.DoesNotContain("缺文件头注释: 1", result);
    }

    [Fact]
    public async Task CodeStyleReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new CodeStyleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new CodeStyleReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("A.cs", "class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodeStyleReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
