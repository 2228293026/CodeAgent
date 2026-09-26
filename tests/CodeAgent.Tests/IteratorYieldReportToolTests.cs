using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class IteratorYieldReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-iter-" + Guid.NewGuid().ToString("N"));

    public IteratorYieldReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task It_FindsReturnInsideIterator()
    {
        WriteSource(
            "using System.Collections.Generic;", "class A", "{",
            "    public IEnumerable<int> M()", "    {",
            "        yield return 1;", "        return;", "    }", "}");
        var result = await new IteratorYieldReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("迭代器报告: 1 个 .cs 文件", result);
        Assert.Contains("迭代器里用 return 结束", result);
        Assert.Contains("提前截断", result);
    }

    [Fact]
    public async Task It_NormalYieldIsClean()
    {
        WriteSource(
            "using System.Collections.Generic;", "class A", "{",
            "    public IEnumerable<int> M()", "    {",
            "        yield return 1;", "        yield return 2;", "    }", "}");
        var result = await new IteratorYieldReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现迭代器问题", result);
    }

    [Fact]
    public async Task It_FindsBlockingIo()
    {
        WriteSource(
            "using System.Collections.Generic;", "using System.IO;", "class A", "{",
            "    public IEnumerable<string> M(string p)", "    {",
            "        yield return File.ReadAllText(p);", "    }", "}");
        var result = await new IteratorYieldReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("迭代器里阻塞 I/O", result);
        Assert.Contains("物化", result);
    }

    [Fact]
    public async Task It_ReturnInNonIteratorIsClean()
    {
        WriteSource(
            "using System.Collections.Generic;", "class A", "{",
            "    public int M()", "    {", "        return 1;", "    }", "}");
        var result = await new IteratorYieldReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("迭代器里用 return 结束", result);
    }

    [Fact]
    public async Task It_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new IteratorYieldReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new IteratorYieldReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IteratorYieldReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
