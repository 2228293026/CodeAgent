using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DeadBranchReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-deadbr-" + Guid.NewGuid().ToString("N"));

    public DeadBranchReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task DeadBranch_FindsBothBranchesReturning()
    {
        WriteSource(
            "class A",
            "{",
            "    int M(bool f)",
            "    {",
            "        if (f)",
            "        {",
            "            return 1;",
            "        }",
            "        else",
            "        {",
            "            return 2;",
            "        }",
            "    }",
            "}");
        var result = await new DeadBranchReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("分支可达性报告: 1 个 .cs 文件", result);
        Assert.Contains("两分支都 return", result);
    }

    [Fact]
    public async Task DeadBranch_FindsConstantCondition()
    {
        WriteSource("class A", "{", "    void M(bool f)", "    {", "        if (true)", "        {", "        }", "    }", "}");
        var result = await new DeadBranchReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("恒真/恒假条件", result);
    }

    [Fact]
    public async Task DeadBranch_NormalIfElseIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    int M(bool f)",
            "    {",
            "        if (f)",
            "        {",
            "            return 1;",
            "        }",
            "        else",
            "        {",
            "            Console.WriteLine(\"x\");",
            "        }",
            "        return 0;",
            "    }",
            "}");
        var result = await new DeadBranchReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("两分支都 return", result);
    }

    [Fact]
    public async Task DeadBranch_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new DeadBranchReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现分支可达性问题", result);
    }

    [Fact]
    public async Task DeadBranch_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DeadBranchReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DeadBranchReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeadBranchReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
