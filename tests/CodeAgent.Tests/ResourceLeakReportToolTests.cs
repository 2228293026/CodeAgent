using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ResourceLeakReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-resleak-" + Guid.NewGuid().ToString("N"));

    public ResourceLeakReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ResourceLeakReport_FindsMissingUsing()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        var fs = new FileStream(\"a\", FileMode.Open);",
            "    }",
            "}");
        var result = await new ResourceLeakReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("资源释放报告: 1 个文件，1 处资源创建", result);
        Assert.Contains("缺少 using/Dispose: 1", result);
        Assert.Contains("new FileStream 未见 using/Dispose", result);
        Assert.Contains("A.cs:5", result);
    }

    [Theory]
    [InlineData("        using var fs = new FileStream(\"a\", FileMode.Open);")]
    [InlineData("        using (var fs = new FileStream(\"a\", FileMode.Open)) { }")]
    [InlineData("        await using var reader = new StreamReader(path);")]
    public async Task ResourceLeakReport_AcceptsProperlyScopedResources(string line)
    {
        WriteSource("class A", "{", "    void M()", "    {", line, "    }", "}");
        var result = await new ResourceLeakReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("缺少 using/Dispose", result);
    }

    [Theory]
    [InlineData("        var p = new Process();", "new Process")]
    [InlineData("        var fs = new FileStream(\"a\", FileMode.Open);", "new FileStream")]
    [InlineData("        var ms = new MemoryStream();", "new MemoryStream")]
    public async Task ResourceLeakReport_FlagsUnscopedCreations(string line, string expected)
    {
        WriteSource("class A", "{", "    void M()", "    {", line, "    }", "}");
        var result = await new ResourceLeakReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("缺少 using/Dispose: 1", result);
        Assert.Contains($"{expected} 未见 using/Dispose", result);
    }

    [Fact]
    public async Task ResourceLeakReport_AcceptsUsingOnNextLine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        var reader =",
            "            new StreamReader(path);",
            "    }",
            "}");
        // using 可能在下一行；这里没有 using，应报
        var reported = await new ResourceLeakReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("new StreamReader 未见 using/Dispose", reported);
    }

    [Fact]
    public async Task ResourceLeakReport_FindsUndisposedField()
    {
        WriteSource(
            "class A",
            "{",
            "    private readonly FileStream _stream;",
            "}");
        var result = await new ResourceLeakReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("字段未释放: 1", result);
        Assert.Contains("_stream（FileStream）", result);
    }

    [Fact]
    public async Task ResourceLeakReport_DisposedFieldIsNotFlagged()
    {
        WriteSource(
            "class A",
            "{",
            "    private FileStream _stream;",
            "    void D() { _stream?.Dispose(); }",
            "}");
        var result = await new ResourceLeakReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("字段未释放", result);
    }

    [Fact]
    public async Task ResourceLeakReport_CleanCodeHasNoLeaks()
    {
        WriteSource("class A", "{", "    int V => 42;", "}");
        var result = await new ResourceLeakReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现疑似资源泄漏", result);
    }

    [Fact]
    public async Task ResourceLeakReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ResourceLeakReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ResourceLeakReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ResourceLeakReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
