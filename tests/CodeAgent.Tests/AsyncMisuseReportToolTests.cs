using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class AsyncMisuseReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-async-" + Guid.NewGuid().ToString("N"));

    public AsyncMisuseReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Async_FindsSyncBlocking()
    {
        WriteSource(
            "using System.Threading.Tasks;", "class A", "{",
            "    int M()", "    {", "        var x = GetAsync().Result;", "        return x;", "    }",
            "    async Task<int> GetAsync() { return 1; }", "}");
        var result = await new AsyncMisuseReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("async 误用报告: 1 个 .cs 文件", result);
        Assert.Contains("同步阻塞异步", result);
        Assert.Contains("死锁", result);
    }

    [Fact]
    public async Task Async_FindsAsyncWithoutAwait()
    {
        WriteSource(
            "using System.Threading.Tasks;", "class A", "{",
            "    public async Task M()", "    {", "        Go();", "    }", "    void Go() { }", "}");
        var result = await new AsyncMisuseReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("async 里没有 await", result);
    }

    [Fact]
    public async Task Async_RealAwaitIsClean()
    {
        WriteSource(
            "using System.Threading.Tasks;", "class A", "{",
            "    public async Task M()", "    {", "        await GoAsync();", "    }",
            "    async Task GoAsync() { }", "}");
        var result = await new AsyncMisuseReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("async 里没有 await", result);
    }

    [Fact]
    public async Task Async_FindsFireAndForget()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        DoAsync();", "    }", "    async Task DoAsync() { }", "}");
        var result = await new AsyncMisuseReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("发射后不管（异常丢失）", result);
        Assert.Contains("静默丢弃", result);
    }

    [Fact]
    public async Task Async_DiscardedTaskIsClean()
    {
        // _ = 是"明确知道自己在丢弃"，和完全不接返回值不是一回事
        WriteSource("class A", "{", "    void M()", "    {", "        _ = DoAsync();", "    }", "    async Task DoAsync() { }", "}");
        var result = await new AsyncMisuseReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("发射后不管（异常丢失）", result);
    }

    [Fact]
    public async Task Async_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new AsyncMisuseReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new AsyncMisuseReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AsyncMisuseReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
