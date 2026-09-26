using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DisposablePatternReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-dispose-" + Guid.NewGuid().ToString("N"));

    public DisposablePatternReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Disposable_FindsFinalizerWithoutSuppress()
    {
        WriteSource(
            "class Res : IDisposable",
            "{",
            "    ~Res() { }",
            "    public void Dispose() { }",
            "}");
        var result = await new DisposablePatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("IDisposable 报告: 1 个 .cs 文件", result);
        Assert.Contains("有终结器但未抑制", result);
        Assert.Contains("GC.SuppressFinalize", result);
    }

    [Fact]
    public async Task Disposable_FinalizerWithSuppressIsFine()
    {
        WriteSource(
            "class Res : IDisposable",
            "{",
            "    ~Res() { }",
            "    public void Dispose() { GC.SuppressFinalize(this); }",
            "}");
        var result = await new DisposablePatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("有终结器但未抑制", result);
    }

    [Fact]
    public async Task Disposable_FindsUndisposedStreamField()
    {
        WriteSource(
            "class A",
            "{",
            "    private readonly System.IO.Stream _stream = System.IO.Stream.Null;",
            "}");
        var result = await new DisposablePatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("字段未释放", result);
        Assert.Contains("泄漏句柄", result);
    }

    [Fact]
    public async Task Disposable_DisposedStreamFieldIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    private readonly System.IO.Stream _stream = System.IO.Stream.Null;",
            "    public void Dispose() => _stream.Dispose();",
            "}");
        var result = await new DisposablePatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("字段未释放", result);
    }

    [Fact]
    public async Task Disposable_FindsMissingDisposeImplementation()
    {
        WriteSource("class Res : IDisposable", "{", "    public int V => 3;", "}");
        var result = await new DisposablePatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("缺少 Dispose 实现", result);
    }

    [Fact]
    public async Task Disposable_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new DisposablePatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 IDisposable 问题", result);
    }

    [Fact]
    public async Task Disposable_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DisposablePatternReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DisposablePatternReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DisposablePatternReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
