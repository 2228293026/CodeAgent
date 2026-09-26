using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CacheStrategyReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-cache-" + Guid.NewGuid().ToString("N"));

    public CacheStrategyReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task CacheReport_FindsUnboundedCache()
    {
        WriteSource(
            "class A",
            "{",
            "    static readonly System.Collections.Generic.Dictionary<string, string> Cache = new();",
            "}");
        var result = await new CacheStrategyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("缓存策略报告: 1 个 .cs 文件", result);
        Assert.Contains("无界缓存: 1", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task CacheReport_BoundedCacheIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    static readonly System.Collections.Generic.Dictionary<string, string> Cache = new();",
            "",
            "    void Put(string k, string v)",
            "    {",
            "        if (Cache.Count > 100) Cache.Clear();",
            "        Cache[k] = v;",
            "    }",
            "}");
        var result = await new CacheStrategyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("无界缓存", result);
    }

    [Fact]
    public async Task CacheReport_FindsRepeatedComputation()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(string p)",
            "    {",
            "        var a = GetValue(p);",
            "        var b = GetValue(p);",
            "        var c = GetValue(p);",
            "    }",
            "}");
        var result = await new CacheStrategyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重复计算: 1", result);
        Assert.Contains("可提升为局部变量", result);
    }

    [Fact]
    public async Task CacheReport_SingleCallIsFine()
    {
        WriteSource("class A", "{", "    void M(string p) => GetValue(p);", "}");
        var result = await new CacheStrategyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("重复计算", result);
    }

    [Fact]
    public async Task CacheReport_FindsNonUniqueKey()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(int i)",
            "    {",
            "        foreach (var x in xs)",
            "            dict[i] = x;",
            "    }",
            "}");
        var result = await new CacheStrategyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("缓存键不唯一", result);
        Assert.Contains("跨调用会互相覆盖", result);
    }

    [Fact]
    public async Task CacheReport_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new CacheStrategyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现缓存策略问题", result);
    }

    [Fact]
    public async Task CacheReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new CacheStrategyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new CacheStrategyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CacheStrategyReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
