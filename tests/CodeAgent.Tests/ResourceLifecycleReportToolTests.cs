using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ResourceLifecycleReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-reslifecycle-" + Guid.NewGuid().ToString("N"));

    public ResourceLifecycleReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ResourceLifecycle_FindsUndisposableObject()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        var s = new System.IO.MemoryStream();", "    }", "}");
        var result = await new ResourceLifecycleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("资源生命周期报告: 1 个 .cs 文件", result);
        Assert.Contains("可释放对象未托管: 1", result);
        Assert.Contains("A.cs:5", result);
    }

    [Fact]
    public async Task ResourceLifecycle_UsingDeclarationIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        using var s = new System.IO.MemoryStream();",
            "    }",
            "}");
        var result = await new ResourceLifecycleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("可释放对象未托管", result);
    }

    [Fact]
    public async Task ResourceLifecycle_FindsMissingUnsubscribe()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        source.Changed += Handler;", "    }", "}");
        var result = await new ResourceLifecycleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("订阅未退订", result);
        Assert.Contains("对象无法回收", result);
    }

    [Fact]
    public async Task ResourceLifecycle_PairedUnsubscribeIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        source.Changed += Handler;",
            "        source.Changed -= Handler;",
            "    }",
            "}");
        var result = await new ResourceLifecycleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("订阅未退订", result);
    }

    [Fact]
    public async Task ResourceLifecycle_FindsFinalizerWithoutDispose()
    {
        WriteSource("class A", "{", "    ~A()", "    {", "        Cleanup();", "    }", "}");
        var result = await new ResourceLifecycleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("只有终结器无 Dispose", result);
        Assert.Contains("资源释放依赖 GC", result);
    }

    [Fact]
    public async Task ResourceLifecycle_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new ResourceLifecycleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现资源生命周期问题", result);
    }

    [Fact]
    public async Task ResourceLifecycle_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ResourceLifecycleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ResourceLifecycleReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ResourceLifecycleReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
