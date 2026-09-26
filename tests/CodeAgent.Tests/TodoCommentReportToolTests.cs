using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TodoCommentReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-todo-" + Guid.NewGuid().ToString("N"));

    public TodoCommentReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Todo_FindsMarkerWithoutOwner()
    {
        WriteSource("class A", "{", "    // TODO 以后再改", "}");
        var result = await new TodoCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("TODO 标记报告: 1 个 .cs 文件", result);
        Assert.Contains("无归属人", result);
        Assert.Contains("不知道该找谁", result);
    }

    [Fact]
    public async Task Todo_FindsMarkerWithoutTicket()
    {
        WriteSource("class A", "{", "    // TODO @alice 以后再改", "}");
        var result = await new TodoCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("无关联工单", result);
        Assert.DoesNotContain("无归属人", result);
    }

    [Fact]
    public async Task Todo_CompleteMarkerIsFine()
    {
        WriteSource("class A", "{", "    // TODO @alice #123 以后再改", "}");
        var result = await new TodoCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 TODO 标记问题", result);
    }

    [Fact]
    public async Task Todo_FindsMarkerInDeadComment()
    {
        WriteSource("class A", "{", "    //// TODO 这个功能已经删了", "}");
        var result = await new TodoCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("在被注释掉的代码里", result);
        Assert.Contains("永远不会有人清理", result);
    }

    [Fact]
    public async Task Todo_FindsOtherMarkerKinds()
    {
        WriteSource("class A", "{", "    // FIXME @bob #7 修一下", "    // HACK @bob #8 临时", "}");
        var result = await new TodoCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("TODO 标记报告: 1 个 .cs 文件，2 处标记", result);
    }

    [Fact]
    public async Task Todo_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new TodoCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 TODO 标记问题", result);
    }

    [Fact]
    public async Task Todo_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new TodoCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new TodoCommentReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TodoCommentReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
