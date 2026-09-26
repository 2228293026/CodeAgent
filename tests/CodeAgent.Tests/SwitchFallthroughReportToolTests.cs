using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class SwitchFallthroughReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-sw-" + Guid.NewGuid().ToString("N"));

    public SwitchFallthroughReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Sw_FindsFallthrough()
    {
        WriteSource(
            "class A", "{", "    void M(int x)", "    {", "        switch (x)", "        {",
            "            case 1:", "                A();",
            "            case 2:", "                B();", "                break;",
            "        }", "    }", "    void A() { }", "    void B() { }", "}");
        var result = await new SwitchFallthroughReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("switch 穿透报告: 1 个 .cs 文件", result);
        Assert.Contains("case 分支穿透", result);
        Assert.Contains("漏写 break", result);
    }

    [Fact]
    public async Task Sw_AllTerminatedIsClean()
    {
        WriteSource(
            "class A", "{", "    void M(int x)", "    {", "        switch (x)", "        {",
            "            case 1:", "                A();", "                break;",
            "            case 2:", "                return;", "            default:", "                throw new E();",
            "        }", "    }", "    void A() { }", "    class E : Exception { }", "}");
        var result = await new SwitchFallthroughReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 switch 穿透问题", result);
    }

    [Fact]
    public async Task Sw_GotoIsNotFallthrough()
    {
        WriteSource(
            "class A", "{", "    void M(int x)", "    {", "        switch (x)", "        {",
            "            case 1:", "                A();", "                break;",
            "            case 2:", "                goto case 1;",
            "            default:", "                break;",
            "        }", "    }", "    void A() { }", "}");
        var result = await new SwitchFallthroughReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("case 分支穿透", result);
    }

    [Fact]
    public async Task Sw_FindsDefaultNotLast()
    {
        WriteSource(
            "class A", "{", "    void M(int x)", "    {", "        switch (x)", "        {",
            "            default:", "                A();", "                break;",
            "            case 1:", "                B();", "                break;",
            "        }", "    }", "    void A() { }", "    void B() { }", "}");
        var result = await new SwitchFallthroughReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("default 不在最后", result);
    }

    [Fact]
    public void TheTerminatorListCoversEveryBranchExit()
    {
        Assert.True(SwitchFallthroughReportTool.EndsWithTerminator("break;"));
        Assert.True(SwitchFallthroughReportTool.EndsWithTerminator("return 1;"));
        Assert.True(SwitchFallthroughReportTool.EndsWithTerminator("throw new E();"));
        Assert.True(SwitchFallthroughReportTool.EndsWithTerminator("goto case 1;"));
        Assert.False(SwitchFallthroughReportTool.EndsWithTerminator("A();"));
    }

    [Fact]
    public async Task Sw_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new SwitchFallthroughReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new SwitchFallthroughReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SwitchFallthroughReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
