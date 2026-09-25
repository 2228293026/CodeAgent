using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TodoMarkerReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-todo-" + Guid.NewGuid().ToString("N"));

    public TodoMarkerReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    [Fact]
    public async Task TodoMarkerReport_CountsTagsAndListsHits()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "// TODO: 修复\nvar x = 1; // FIXME 后续\n");
        File.WriteAllText(Path.Combine(_dir, "b.md"), "TODO 文档\n");
        var result = await new TodoMarkerReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("待办标记报告: 3 处", result);
        Assert.Contains("TODO: 2", result);
        Assert.Contains("FIXME: 1", result);
        Assert.Contains("[TODO] a.cs:1 // TODO: 修复", result);
        Assert.Contains("[TODO] b.md:1 TODO 文档", result);
    }

    [Fact]
    public async Task TodoMarkerReport_TagFilterSelectsSubset()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "// TODO a\n// HACK b\n");
        var args = new JsonObject
        {
            ["tags"] = new JsonArray("HACK"),
        };
        var result = await new TodoMarkerReportTool().ExecuteAsync(args, Context(), CancellationToken.None);
        Assert.Contains("待办标记报告: 1 处", result);
        Assert.Contains("HACK: 1", result);
        Assert.DoesNotContain("TODO", result);
    }

    [Fact]
    public async Task TodoMarkerReport_RejectsBadTagOutsideAndCancellation()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "// TODO a\n");
        await Assert.ThrowsAsync<ToolException>(() => new TodoMarkerReportTool().ExecuteAsync(
            new JsonObject { ["tags"] = new JsonArray("NOPE") }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new TodoMarkerReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TodoMarkerReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }

    [Fact]
    public async Task TodoMarkerReport_EmptyTreeReportsZero()
    {
        var result = await new TodoMarkerReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("待办标记报告: 0 处", result);
    }
}
