using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ExpensiveCallReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-expcall-" + Guid.NewGuid().ToString("N"));

    public ExpensiveCallReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteFile(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    [Fact]
    public async Task ExpensiveCall_FindsCallInsideLoop()
    {
        WriteFile(
            "A.cs",
            "class A",
            "{",
            "    void M(System.Collections.Generic.List<string> xs)",
            "    {",
            "        foreach (var x in xs)",
            "        {",
            "            var v = ParseValue(x);",
            "        }",
            "    }",
            "}");
        var result = await new ExpensiveCallReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("调用开销报告: 1 个 .cs 文件", result);
        Assert.Contains("循环内昂贵调用", result);
        Assert.Contains("ParseValue", result);
    }

    [Fact]
    public async Task ExpensiveCall_CallOutsideLoopIsFine()
    {
        WriteFile("A.cs", "class A", "{", "    void M(string s) => ParseValue(s);", "}");
        var result = await new ExpensiveCallReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("循环内昂贵调用", result);
    }

    [Fact]
    public async Task ExpensiveCall_FindsRegexConstructedInLoop()
    {
        WriteFile(
            "A.cs",
            "class A",
            "{",
            "    void M(System.Collections.Generic.List<string> xs)",
            "    {",
            "        foreach (var x in xs)",
            "        {",
            "            var re = new System.Text.RegularExpressions.Regex(x);",
            "        }",
            "    }",
            "}");
        var result = await new ExpensiveCallReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("循环内 new Regex", result);
        Assert.Contains("重新构造", result);
    }

    [Fact]
    public async Task ExpensiveCall_CompiledRegexIsFine()
    {
        WriteFile(
            "A.cs",
            "class A",
            "{",
            "    void M(System.Collections.Generic.List<string> xs)",
            "    {",
            "        foreach (var x in xs)",
            "        {",
            "            var re = new System.Text.RegularExpressions.Regex(x, System.Text.RegularExpressions.RegexOptions.Compiled);",
            "        }",
            "    }",
            "}");
        var result = await new ExpensiveCallReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("循环内 new Regex", result);
    }

    [Fact]
    public async Task ExpensiveCall_FindsScatteredCall()
    {
        for (var i = 1; i <= 3; i++)
            WriteFile($"F{i}.cs", "class C" + i, "{", "    void M() => LoadSettings();", "}");
        var result = await new ExpensiveCallReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("调用散落多处", result);
        Assert.Contains("LoadSettings", result);
    }

    [Fact]
    public async Task ExpensiveCall_CleanCodeHasNoIssues()
    {
        WriteFile("A.cs", "class A", "{", "    int V => 3;", "}");
        var result = await new ExpensiveCallReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现调用开销问题", result);
    }

    [Fact]
    public async Task ExpensiveCall_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ExpensiveCallReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ExpensiveCallReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteFile("A.cs", "class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExpensiveCallReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
