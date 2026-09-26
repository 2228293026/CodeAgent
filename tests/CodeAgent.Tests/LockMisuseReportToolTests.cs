using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LockMisuseReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-lock-" + Guid.NewGuid().ToString("N"));

    public LockMisuseReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Lock_FindsAwaitInsideLock()
    {
        WriteSource(
            "class A", "{", "    object gate = new();", "    async Task M()", "    {",
            "        lock (gate)", "        {", "            await N();", "        }", "    }",
            "    Task N() => Task.CompletedTask;", "}");
        var result = await new LockMisuseReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("锁使用报告: 1 个 .cs 文件", result);
        Assert.Contains("lock 块里有 await", result);
        Assert.Contains("SemaphoreSlim", result);
    }

    [Fact]
    public async Task Lock_FindsValueTypeLock()
    {
        WriteSource("class A", "{", "    int counter;", "    void M()", "    {", "        lock (counter)", "        {", "            counter++;", "        }", "    }", "}");
        var result = await new LockMisuseReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("锁了值类型/字面量", result);
    }

    [Fact]
    public async Task Lock_FindsNestedSameLock()
    {
        WriteSource(
            "class A", "{", "    object gate = new();", "    void M()", "    {",
            "        lock (gate)", "        {", "            lock (gate)", "            {", "                N();", "            }", "        }", "    }",
            "    void N() { }", "}");
        var result = await new LockMisuseReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("同一把锁嵌套", result);
    }

    [Fact]
    public async Task Lock_SequentialLocksAreNotNested()
    {
        // 关键回归：先后两段 lock 不是嵌套——报成嵌套就等于噪声
        WriteSource(
            "class A", "{", "    object gate = new();", "    void M()", "    {",
            "        lock (gate)", "        {", "            N();", "        }",
            "        lock (gate)", "        {", "            N();", "        }", "    }", "    void N() { }", "}");
        var result = await new LockMisuseReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("同一把锁嵌套", result);
        Assert.Contains("未发现锁使用问题", result);
    }

    [Fact]
    public async Task Lock_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new LockMisuseReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new LockMisuseReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LockMisuseReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
