using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class NullAssignmentReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-null-" + Guid.NewGuid().ToString("N"));

    public NullAssignmentReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Null_FindsNonNullFieldAssignedNull()
    {
        WriteSource("class A", "{", "    private string name;", "    void M()", "    {", "        name = null;", "    }", "}");
        var result = await new NullAssignmentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("可空性报告: 1 个 .cs 文件", result);
        Assert.Contains("非空变量被赋 null", result);
        Assert.Contains("name", result);
    }

    [Fact]
    public async Task Null_FindsDerefAfterNullCheck()
    {
        WriteSource(
            "class A", "{", "    void M(Config c)", "    {",
            "        if (c == null)", "        {", "            c.Load();", "        }", "    }", "}");
        var result = await new NullAssignmentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("判空后仍直接解引用", result);
    }

    [Fact]
    public async Task Null_GuardThatReturnsIsFine()
    {
        WriteSource(
            "class A", "{", "    void M(Config c)", "    {",
            "        if (c == null)", "        {", "            return;", "        }", "        c?.Load();", "    }", "}");
        var result = await new NullAssignmentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现可空性陷阱", result);
    }

    [Fact]
    public async Task Null_NullableFieldAssignedNullIsFine()
    {
        WriteSource("class A", "{", "    private string? name;", "    void M()", "    {", "        name = null;", "    }", "}");
        var result = await new NullAssignmentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现可空性陷阱", result);
    }

    [Fact]
    public async Task Null_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new NullAssignmentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new NullAssignmentReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NullAssignmentReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
