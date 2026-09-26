using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class BoxingAllocationReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-box-" + Guid.NewGuid().ToString("N"));

    public BoxingAllocationReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Boxing_FindsBoxedValue()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        object o = 42;", "    }", "}");
        var result = await new BoxingAllocationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("隐式分配报告: 1 个 .cs 文件", result);
        Assert.Contains("装箱", result);
    }

    [Fact]
    public async Task Boxing_FindsInterpolationInsideLoop()
    {
        WriteSource(
            "class A", "{", "    void M(int[] xs)", "    {",
            "        foreach (var x in xs)", "        {", "            var s = $\"值 {x}\";", "        }", "    }", "}");
        var result = await new BoxingAllocationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("循环体内的字符串插值", result);
    }

    [Fact]
    public async Task Boxing_NestedIfDoesNotEndLoopScope()
    {
        // 回归点：循环体里套一个 if，if 的 '}' 不得把外层循环弹掉
        WriteSource(
            "class A", "{", "    void M(int[] xs)", "    {",
            "        for (int i = 0; i < 3; i++)", "        {",
            "            if (i > 0)", "            {", "                Console.Write(1);", "            }",
            "            var s = $\"v{i}\";",
            "        }", "    }", "}");
        var result = await new BoxingAllocationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("循环体内的字符串插值", result);
    }

    [Fact]
    public async Task Boxing_OutsideLoopIsNotReported()
    {
        WriteSource("class A", "{", "    int M(int x)", "    {", "        var s = $\"v{x}\";", "        return s.Length;", "    }", "}");
        var result = await new BoxingAllocationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("循环体内的字符串插值", result);
    }

    [Fact]
    public async Task Boxing_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new BoxingAllocationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new BoxingAllocationReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BoxingAllocationReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
