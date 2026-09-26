using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LifetimeBoundsReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-bounds-" + Guid.NewGuid().ToString("N"));

    public LifetimeBoundsReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Bounds_FindsArraySizedByArgument()
    {
        WriteSource("class A", "{", "    byte[] M(int count) => new byte[count];", "}");
        var result = await new LifetimeBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("容量边界报告: 1 个 .cs 文件", result);
        Assert.Contains("按入参分配数组", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task Bounds_ClampedAllocationIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    byte[] M(int count)",
            "    {",
            "        count = Math.Clamp(count, 0, 1024);",
            "        return new byte[count];",
            "    }",
            "}");
        var result = await new LifetimeBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("按入参分配数组", result);
    }

    [Fact]
    public async Task Bounds_FindsUnboundedCollect()
    {
        WriteSource(
            "class A",
            "{",
            "    object[] M(System.Collections.Generic.List<string> files)",
            "    {",
            "        return files.Select(f => Open(f)).ToArray();",
            "    }",
            "}");
        var result = await new LifetimeBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("无上限收集", result);
        Assert.Contains("Take", result);
    }

    [Fact]
    public async Task Bounds_TakeLimitedCollectIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    object[] M(System.Collections.Generic.List<string> items)",
            "    {",
            "        return items.Take(100).Select(f => Open(f)).ToArray();",
            "    }",
            "}");
        var result = await new LifetimeBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("无上限收集", result);
    }

    [Fact]
    public async Task Bounds_FindsMagicNumberThreshold()
    {
        WriteSource("class A", "{", "    bool M(int n) => n > 1024;", "}");
        var result = await new LifetimeBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("魔法数字阈值", result);
        Assert.Contains("1024", result);
        Assert.Contains("命名常量", result);
    }

    [Fact]
    public async Task Bounds_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new LifetimeBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现容量边界问题", result);
    }

    [Fact]
    public async Task Bounds_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new LifetimeBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new LifetimeBoundsReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LifetimeBoundsReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
