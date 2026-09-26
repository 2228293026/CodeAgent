using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LostUpdateReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-lu-" + Guid.NewGuid().ToString("N"));

    public LostUpdateReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Lu_FindsUnsyncedIncrement()
    {
        WriteSource(
            "class A", "{", "    private static int _counter;", "    void M()", "    {", "        _counter++;", "    }", "}");
        var result = await new LostUpdateReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("丢失更新报告: 1 个 .cs 文件", result);
        Assert.Contains("共享字段自增无同步", result);
        Assert.Contains("计数比实际少", result);
    }

    [Fact]
    public async Task Lu_FindsReadModifyWrite()
    {
        WriteSource(
            "class A", "{", "    private static int _count;", "    void M()", "    {", "        _count = _count + 1;", "    }", "}");
        var result = await new LostUpdateReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("共享字段读改写无同步", result);
        Assert.Contains("读-改-写", result);
    }

    [Fact]
    public async Task Lu_LockedIncrementIsClean()
    {
        WriteSource(
            "class A", "{", "    private static int _counter;", "    void M()", "    {",
            "        lock (gate)", "        {", "            _counter++;", "        }", "    }", "    object gate = new();", "}");
        var result = await new LostUpdateReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现丢失更新问题", result);
    }

    [Fact]
    public async Task Lu_InterlockedIsClean()
    {
        WriteSource(
            "using System.Threading;", "class A", "{", "    private static int _counter;",
            "    void M()", "    {", "        Interlocked.Increment(ref _counter);", "    }", "}");
        var result = await new LostUpdateReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现丢失更新问题", result);
    }

    [Fact]
    public async Task Lu_PlainLocalIsClean()
    {
        // 局部变量不是共享的，自增完全正常
        WriteSource("class A", "{", "    void M()", "    {", "        int n = 0;", "        n++;", "    }", "}");
        var result = await new LostUpdateReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现丢失更新问题", result);
    }

    [Fact]
    public void TheSharedFieldHeuristicIsDocumented()
    {
        Assert.True(LostUpdateReportTool.IsShared("private static ", "_counter"));
        Assert.True(LostUpdateReportTool.IsShared("private ", "_count"));
        Assert.False(LostUpdateReportTool.IsShared("private ", "count"));
    }

    [Fact]
    public async Task Lu_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new LostUpdateReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new LostUpdateReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LostUpdateReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
