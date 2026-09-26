using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class StateManagementReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-state-" + Guid.NewGuid().ToString("N"));

    public StateManagementReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task StateReport_FindsSingletonWithMutableCollection()
    {
        WriteSource(
            "class Registry",
            "{",
            "    static readonly Registry Instance = new();",
            "    readonly System.Collections.Generic.Dictionary<string, string> map = new();",
            "}");
        var result = await new StateManagementReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("状态管理报告: 1 个 .cs 文件", result);
        Assert.Contains("单例持有可变集合", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task StateReport_FindsExposedMutableCollection()
    {
        WriteSource(
            "class Store",
            "{",
            "    readonly System.Collections.Generic.List<string> _items = new();",
            "    public System.Collections.Generic.List<string> Items => _items;",
            "}");
        var result = await new StateManagementReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("暴露可变集合", result);
        Assert.Contains("只读视图", result);
    }

    [Fact]
    public async Task StateReport_FindsIncompleteReset()
    {
        WriteSource(
            "class Session",
            "{",
            "    private int _a;",
            "    private int _b;",
            "    private int _c;",
            "    private int _d;",
            "",
            "    public void Reset()",
            "    {",
            "        _a = 0;",
            "    }",
            "}");
        var result = await new StateManagementReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Reset 覆盖不全", result);
        Assert.Contains("只清了", result);
    }

    [Fact]
    public async Task StateReport_CompleteResetIsFine()
    {
        WriteSource(
            "class Session",
            "{",
            "    private int _a;",
            "    private int _b;",
            "",
            "    public void Reset()",
            "    {",
            "        _a = 0;",
            "        _b = 0;",
            "    }",
            "}");
        var result = await new StateManagementReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("Reset 覆盖不全", result);
    }

    [Fact]
    public async Task StateReport_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new StateManagementReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现状态管理问题", result);
    }

    [Fact]
    public async Task StateReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new StateManagementReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new StateManagementReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StateManagementReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
