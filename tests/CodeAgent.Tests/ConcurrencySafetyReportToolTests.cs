using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ConcurrencySafetyReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-conc-" + Guid.NewGuid().ToString("N"));

    public ConcurrencySafetyReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ConcurrencySafety_FindsLockHeldAcrossAwait()
    {
        WriteSource(
            "class A",
            "{",
            "    async System.Threading.Tasks.Task M()",
            "    {",
            "        lock (gate)",
            "        {",
            "            await RiskyAsync();",
            "        }",
            "    }",
            "}");
        var result = await new ConcurrencySafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("并发安全报告: 1 个 .cs 文件", result);
        Assert.Contains("跨 await 持锁", result);
        Assert.Contains("可能死锁", result);
    }

    [Fact]
    public async Task ConcurrencySafety_SyncWorkUnderLockIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        lock (gate)",
            "        {",
            "            counter++;",
            "        }",
            "    }",
            "}");
        var result = await new ConcurrencySafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("跨 await 持锁", result);
    }

    [Fact]
    public async Task ConcurrencySafety_FindsUnprotectedSharedWrite()
    {
        WriteSource(
            "class A",
            "{",
            "    readonly System.Collections.Generic.List<string> Items = new();",
            "",
            "    void M(string s)",
            "    {",
            "        Items.Add(s);",
            "    }",
            "}");
        var result = await new ConcurrencySafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("无保护写共享集合", result);
        Assert.Contains("Items.Add", result);
    }

    [Fact]
    public async Task ConcurrencySafety_LockedWriteIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    readonly System.Collections.Generic.List<string> Items = new();",
            "",
            "    void M(string s)",
            "    {",
            "        lock (gate)",
            "        {",
            "            Items.Add(s);",
            "        }",
            "    }",
            "}");
        var result = await new ConcurrencySafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("无保护写共享集合", result);
    }

    [Fact]
    public async Task ConcurrencySafety_FindsRawThread()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        var t = new System.Threading.Thread(Work);", "        t.Start();", "    }", "}");
        var result = await new ConcurrencySafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("裸线程缺异常处理", result);
        Assert.Contains("终止进程", result);
    }

    [Fact]
    public async Task ConcurrencySafety_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new ConcurrencySafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现并发安全问题", result);
    }

    [Fact]
    public async Task ConcurrencySafety_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ConcurrencySafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ConcurrencySafetyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConcurrencySafetyReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
