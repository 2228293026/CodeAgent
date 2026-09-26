using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class StaleCommentReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-stale-" + Guid.NewGuid().ToString("N"));

    public StaleCommentReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Stale_FindsRestatedComment()
    {
        // 判据是「所有有实义的词都来自标识符」：`// Total` 只是复述，
        // 而下面那条测试里的 `// The Total of all items` 解释了「是什么」，不算复述。
        WriteSource("class A", "{", "    // Total", "    int Total() => 1;", "}");
        var result = await new StaleCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("脱节注释报告: 1 个 .cs 文件", result);
        Assert.Contains("复述式注释", result);
        Assert.Contains("Total", result);
    }

    [Fact]
    public async Task Stale_CommentWithTheNamePlusRealWordsIsFine()
    {
        WriteSource("class A", "{", "    // The Total of all items", "    int Total() => 1;", "}");
        var result = await new StaleCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("复述式注释", result);
    }

    [Fact]
    public async Task Stale_RealExplanationIsFine()
    {
        WriteSource("class A", "{", "    // 先按时间升序排，再对同一时刻的条目保持稳定的先后次序", "    int Total() => 1;", "}");
        var result = await new StaleCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("复述式注释", result);
    }

    [Fact]
    public async Task Stale_FindsCommentedOutCode()
    {
        WriteSource("class A", "{", "    // var old = Compute();", "    int V => 1;", "}");
        var result = await new StaleCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("注释掉的死代码", result);
    }

    [Fact]
    public async Task Stale_FindsTodoMarker()
    {
        WriteSource("class A", "{", "    // TODO: 这里还没处理超时", "    int V => 1;", "}");
        var result = await new StaleCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("待办/过期标记", result);
        Assert.Contains("TODO", result);
    }

    [Fact]
    public async Task Stale_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 1;", "}");
        var result = await new StaleCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现脱节注释", result);
    }

    [Fact]
    public async Task Stale_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new StaleCommentReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new StaleCommentReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StaleCommentReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
