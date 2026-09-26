using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class UndisposedStreamReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-stream-" + Guid.NewGuid().ToString("N"));

    public UndisposedStreamReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Stream_FindsUnwrappedStream()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        var fs = new FileStream(\"a\", FileMode.Open);", "    }", "}");
        var result = await new UndisposedStreamReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("流泄漏报告: 1 个 .cs 文件", result);
        Assert.Contains("既没有 using 也没有 Dispose", result);
        Assert.Contains("FileStream", result);
    }

    [Fact]
    public async Task Stream_UsingIsFine()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        using var fs = new FileStream(\"a\", FileMode.Open);", "    }", "}");
        var result = await new UndisposedStreamReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现流泄漏风险", result);
    }

    [Fact]
    public async Task Stream_ManualDisposeIsFlaggedSeparately()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {",
            "        var fs = new FileStream(\"a\", FileMode.Open);",
            "        fs.Dispose();",
            "    }", "}");
        var result = await new UndisposedStreamReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("手动 Dispose/Close", result);
        Assert.Contains("异常路径上会漏掉", result);
    }

    [Fact]
    public async Task Stream_DirectReturnIsFlagged()
    {
        WriteSource("class A", "{", "    StreamReader M() => new StreamReader(\"a\");", "}");
        var result = await new UndisposedStreamReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("直接返回可释放对象", result);
        Assert.Contains("StreamReader", result);
    }

    [Fact]
    public async Task Stream_MemoryStreamIsNotTracked()
    {
        // MemoryStream 不占系统句柄，报出来只会淹没真问题
        Assert.DoesNotContain("MemoryStream", UndisposedStreamReportTool.DisposableTypes);
        WriteSource("class A", "{", "    void M()", "    {", "        var ms = new MemoryStream();", "    }", "}");
        var result = await new UndisposedStreamReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现流泄漏风险", result);
    }

    [Fact]
    public async Task Stream_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new UndisposedStreamReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new UndisposedStreamReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UndisposedStreamReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
