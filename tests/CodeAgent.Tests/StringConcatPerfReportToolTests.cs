using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class StringConcatPerfReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-concat-" + Guid.NewGuid().ToString("N"));

    public StringConcatPerfReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Concat_FindsPlusAssignInLoop()
    {
        WriteSource(
            "class A", "{", "    string Join()", "    {",
            "        var s = \"\";",
            "        foreach (var x in new[] { 1, 2 })", "        {", "            s += x;",
            "        }", "        return s;", "    }", "}");
        var result = await new StringConcatPerfReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("字符串拼接性能报告: 1 个 .cs 文件", result);
        Assert.Contains("循环内的 += 拼接", result);
        Assert.Contains("StringBuilder", result);
    }

    [Fact]
    public async Task Concat_NestedIfDoesNotEndLoopScope()
    {
        // 关键回归点：循环体里套一个 if，if 的 '}' 不得把外层循环的栈帧弹掉，
        // 否则 if 之后的行会被误判成"不在循环里"。
        WriteSource(
            "class A", "{", "    void M(int[] xs)", "    {",
            "        for (int i = 0; i < 3; i++)", "        {",
            "            if (i > 0)", "            {", "                Console.Write(1);", "            }",
            "            var s = \"a\" + i;",
            "        }", "    }", "}");
        var result = await new StringConcatPerfReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("在循环体内用 s =", result);
    }

    [Fact]
    public async Task Concat_OutsideLoopIsNotReported()
    {
        WriteSource("class A", "{", "    string M()", "    {", "        var s = \"a\" + 1;", "        return s;", "    }", "}");
        var result = await new StringConcatPerfReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("在循环体内用", result);
    }

    [Fact]
    public async Task Concat_FindsLongManyPlusLine()
    {
        var long1 = "\"aaaaaaaaaa\" + \"bbbbbbbbbb\" + \"cccccccccc\" + \"dddddddddd\" + \"eeeeeeeeee\" + \"ffffffffff\" + \"gggg\";";
        WriteSource("class A", "{", "    string V => " + long1, "}");
        var result = await new StringConcatPerfReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("超长多段拼接", result);
        Assert.Contains("插值", result);
    }

    [Fact]
    public async Task Concat_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new StringConcatPerfReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现字符串拼接性能问题", result);
    }

    [Fact]
    public async Task Concat_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new StringConcatPerfReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new StringConcatPerfReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StringConcatPerfReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
