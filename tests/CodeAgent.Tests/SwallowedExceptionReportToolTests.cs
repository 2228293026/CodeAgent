using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class SwallowedExceptionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-swallow-" + Guid.NewGuid().ToString("N"));

    public SwallowedExceptionReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Swallow_FindsEmptyCatch()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        try { }", "        catch (Exception e) { }", "    }", "}");
        var result = await new SwallowedExceptionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("异常吞噬报告: 1 个 .cs 文件", result);
        Assert.Contains("空 catch 块", result);
        Assert.Contains("完全吞掉", result);
    }

    [Fact]
    public async Task Swallow_FindsSilentCatch()
    {
        WriteSource(
            "class A", "{", "    int M()", "    {",
            "        try { return 1; }", "        catch (IOException e) { e.GetType(); }",
            "        return 0;", "    }", "}");
        var result = await new SwallowedExceptionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("既不抛也不记录", result);
    }

    [Fact]
    public async Task Swallow_FindsMismatchedThrow()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {",
            "        try { }", "        catch (FileNotFoundException)", "        {", "            throw new TimeoutException();", "        }",
            "    }", "}");
        var result = await new SwallowedExceptionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("catch 类型与抛出类型不符", result);
        Assert.Contains("FileNotFoundException", result);
    }

    [Fact]
    public async Task Swallow_ProperHandlingIsFine()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {",
            "        try { }", "        catch (IOException e)", "        {", "            Console.Error.WriteLine(e.Message);", "        }",
            "    }", "}");
        var result = await new SwallowedExceptionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("空 catch 块", result);
        Assert.DoesNotContain("既不抛也不记录", result);
    }

    [Fact]
    public async Task Swallow_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new SwallowedExceptionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new SwallowedExceptionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SwallowedExceptionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
