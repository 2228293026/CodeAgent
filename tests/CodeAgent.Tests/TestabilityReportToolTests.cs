using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TestabilityReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-testab-" + Guid.NewGuid().ToString("N"));

    public TestabilityReportToolTests() => Directory.CreateDirectory(_dir);

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

    private static string[] LongBody(int lines) =>
        Enumerable.Range(0, lines).Select(i => "        var v" + i + " = " + i + ";").ToArray();

    [Fact]
    public async Task Testab_FindsLongMethod()
    {
        var body = new List<string> { "class A", "{", "    public void M()", "    {" };
        body.AddRange(LongBody(70));
        body.Add("    }");
        body.Add("}");
        WriteSource(body.ToArray());
        var result = await new TestabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("可测性报告: 1 个 .cs 文件", result);
        Assert.Contains("过长的方法", result);
        Assert.Contains("M()", result);
    }

    [Fact]
    public async Task Testab_ShortMethodIsClean()
    {
        WriteSource("class A", "{", "    public int M(int x)", "    {", "        return x + 1;", "    }", "}");
        var result = await new TestabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现可测性问题", result);
    }

    [Fact]
    public async Task Testab_FindsLongPrivateMethod()
    {
        var body = new List<string> { "class A", "{", "    private void Helper()", "    {" };
        body.AddRange(LongBody(30));
        body.Add("    }");
        body.Add("}");
        WriteSource(body.ToArray());
        var result = await new TestabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("private 里藏着长逻辑", result);
        Assert.Contains("外部测不到", result);
    }

    [Fact]
    public async Task Testab_FindsUnpairedBranch()
    {
        WriteSource("class A", "{", "    void M(int x)", "    {", "        if (x > 0)", "        {", "            N();", "        }", "    }", "    void N() { }", "}");
        var result = await new TestabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("分支不成对", result);
        Assert.Contains("否定分支从未被显式处理过", result);
    }

    [Fact]
    public void TheThresholdIsReasonable()
    {
        Assert.InRange(TestabilityReportTool.LongMethodLines, 20, 200);
    }

    [Fact]
    public async Task Testab_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new TestabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new TestabilityReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TestabilityReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
