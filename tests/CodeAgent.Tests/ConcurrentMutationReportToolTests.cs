using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ConcurrentMutationReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-conc-" + Guid.NewGuid().ToString("N"));

    public ConcurrentMutationReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Concurrent_FindsSharedCollectionWrite()
    {
        WriteSource(
            "class A", "{", "    void M(int[] xs)", "    {",
            "        Task.WhenAll(xs.Select(x => Task.Run(() =>", "        {", "            shared.Add(x);", "        })));",
            "    }", "}");
        var result = await new ConcurrentMutationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("并发写入报告: 1 个 .cs 文件", result);
        Assert.Contains("并行写共享集合", result);
        Assert.Contains("shared", result);
    }

    [Fact]
    public async Task Concurrent_FindsUnlockedCounter()
    {
        WriteSource(
            "class A", "{", "    void M(int[] xs)", "    {",
            "        Task.WhenAll(xs.Select(x => Task.Run(() =>", "        {", "            count++;", "        })));",
            "    }", "}");
        var result = await new ConcurrentMutationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未加锁的计数器自增", result);
        Assert.Contains("Interlocked", result);
    }

    [Fact]
    public async Task Concurrent_LockedOrLocalIsFine()
    {
        WriteSource(
            "class A", "{", "    void M(int[] xs)", "    {",
            "        var local = new List<int>();",
            "        Task.WhenAll(xs.Select(x => Task.Run(() =>", "        {",
            "            Interlocked.Increment(ref count);",
            "        })));",
            "    }", "}");
        var result = await new ConcurrentMutationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现并发写入问题", result);
    }

    [Fact]
    public async Task Concurrent_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ConcurrentMutationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ConcurrentMutationReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConcurrentMutationReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
