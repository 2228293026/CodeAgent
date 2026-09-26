using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class StateMachineReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-state-" + Guid.NewGuid().ToString("N"));

    public StateMachineReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task State_FindsLiteralOutsideEnum()
    {
        WriteSource(
            "enum Status { Idle, Running }", "class A", "{",
            "    public Status CurrentState { get; set; }",
            "    void M()", "    {", "        CurrentState = \"Runing\";", "    }", "}");
        var result = await new StateMachineReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("状态机报告: 1 个 .cs 文件", result);
        Assert.Contains("赋了枚举外的值", result);
        Assert.Contains("Runing", result);
    }

    [Fact]
    public async Task State_FindsSwitchWithoutDefault()
    {
        WriteSource(
            "enum Status { Idle, Running, Done }", "class A", "{",
            "    public Status CurrentState { get; set; }",
            "    void M(Status CurrentState)", "    {",
            "        switch (CurrentState)", "        {",
            "            case Status.Idle: N(); break;",
            "            case Status.Running: N(); break;",
            "        }", "    }", "    void N() { }", "}");
        var result = await new StateMachineReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("switch 缺 default", result);
    }

    [Fact]
    public async Task State_SwitchWithDefaultIsClean()
    {
        WriteSource(
            "enum Status { Idle, Running, Done }", "class A", "{",
            "    public Status CurrentState { get; set; }",
            "    void M(Status CurrentState)", "    {",
            "        switch (CurrentState)", "        {",
            "            case Status.Idle: N(); break;",
            "            default: N(); break;",
            "        }", "    }", "    void N() { }", "}");
        var result = await new StateMachineReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("switch 缺 default", result);
    }

    [Fact]
    public async Task State_FindsUnguardedTransition()
    {
        WriteSource(
            "enum Status { Idle, Running }", "class A", "{",
            "    public Status CurrentState { get; set; }",
            "    void M()", "    {", "        CurrentState = Status.Running;", "    }", "}");
        var result = await new StateMachineReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("状态变更无前置校验", result);
    }

    [Fact]
    public async Task State_GuardedTransitionIsClean()
    {
        WriteSource(
            "enum Status { Idle, Running }", "class A", "{",
            "    public Status CurrentState { get; set; }",
            "    void M()", "    {",
            "        if (CurrentState == Status.Idle)", "        {", "            CurrentState = Status.Running;", "        }", "    }", "}");
        var result = await new StateMachineReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("状态变更无前置校验", result);
    }

    [Fact]
    public async Task State_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new StateMachineReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new StateMachineReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StateMachineReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
