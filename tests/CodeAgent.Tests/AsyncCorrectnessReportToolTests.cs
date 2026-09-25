using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class AsyncCorrectnessReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-async-" + Guid.NewGuid().ToString("N"));

    public AsyncCorrectnessReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task AsyncCorrectnessReport_FindsAsyncVoid()
    {
        WriteSource(
            "class A",
            "{",
            "    public async void Fire()",
            "    {",
            "        await Task.Delay(1);",
            "    }",
            "}");
        var result = await new AsyncCorrectnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("async void: 1", result);
        Assert.Contains("异常无法被 await 捕获", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task AsyncCorrectnessReport_FindsAsyncWithoutAwait()
    {
        WriteSource(
            "class A",
            "{",
            "    public async Task<int> Compute()",
            "    {",
            "        return 42;",
            "    }",
            "}");
        var result = await new AsyncCorrectnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("无 await 的 async: 1", result);
        Assert.Contains("标了 async 但方法体里没有 await", result);
    }

    [Fact]
    public async Task AsyncCorrectnessReport_FindsTaskWithoutReturn()
    {
        WriteSource(
            "class A",
            "{",
            "    public async Task DoWork()",
            "    {",
            "        await Task.Delay(1);",
            "    }",
            "}");
        var result = await new AsyncCorrectnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Task 无 return: 1", result);
        Assert.Contains("await 结果为 null", result);
    }

    [Fact]
    public async Task AsyncCorrectnessReport_FindsIoWithoutCancellationToken()
    {
        WriteSource(
            "class A",
            "{",
            "    public async Task<string> Load()",
            "    {",
            "        return await File.ReadAllTextAsync(\"a.txt\");",
            "    }",
            "}");
        var result = await new AsyncCorrectnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("I/O 缺 CancellationToken: 1", result);
        Assert.Contains("无法中途取消", result);
    }

    [Fact]
    public async Task AsyncCorrectnessReport_WellFormedMethodHasNoIssues()
    {
        WriteSource(
            "class A",
            "{",
            "    public async Task<int> Compute(CancellationToken ct)",
            "    {",
            "        await Task.Delay(1, ct);",
            "        return 42;",
            "    }",
            "}");
        var result = await new AsyncCorrectnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 async 正确性问题", result);
    }

    [Fact]
    public async Task AsyncCorrectnessReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new AsyncCorrectnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new AsyncCorrectnessReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AsyncCorrectnessReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
