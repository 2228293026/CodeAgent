using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LargeMethodReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-large-" + Guid.NewGuid().ToString("N"));

    public LargeMethodReportToolTests() => Directory.CreateDirectory(_dir);

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

    private static List<string> LongBody(int lines)
    {
        var body = new List<string> { "class A", "{", "    void Big()", "    {" };
        for (var i = 0; i < lines; i++)
            body.Add($"        int v{i} = {i};");
        body.Add("    }");
        body.Add("}");
        return body;
    }

    [Fact]
    public async Task Large_FindsLongMethod()
    {
        WriteSource(LongBody(LargeMethodReportTool.MaxMethodLines + 30).ToArray());
        var result = await new LargeMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("超大方法报告: 1 个 .cs 文件", result);
        Assert.Contains("方法体过长", result);
        Assert.Contains("Big", result);
    }

    [Fact]
    public async Task Large_ShortMethodIsFine()
    {
        WriteSource("class A", "{", "    void Small()", "    {", "        int v = 1;", "    }", "}");
        var result = await new LargeMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现超大方法", result);
    }

    [Fact]
    public async Task Large_FindsTooManyParameters()
    {
        WriteSource("class A", "{", "    void Many(int a, int b, int c, int d, int e, int f, int g)", "    {", "    }", "}");
        var result = await new LargeMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("参数过多", result);
        Assert.Contains("应传对象", result);
    }

    [Fact]
    public async Task Large_FindsDeepNesting()
    {
        WriteSource(
            "class A", "{", "    void Deep()", "    {",
            "        if (1 > 0) {", "            if (1 > 0) {", "                if (1 > 0) {", "                    if (1 > 0) {", "                        if (1 > 0) {", "                        }",
            "                    }", "                }", "            }", "        }", "    }", "}");
        var result = await new LargeMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("嵌套过深", result);
    }

    [Fact]
    public async Task Large_ThresholdsAreSane()
    {
        Assert.InRange(LargeMethodReportTool.MaxMethodLines, 20, 200);
        Assert.InRange(LargeMethodReportTool.MaxNesting, 2, 8);
        Assert.InRange(LargeMethodReportTool.MaxParameters, 3, 10);
    }

    [Fact]
    public async Task Large_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new LargeMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new LargeMethodReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LargeMethodReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
