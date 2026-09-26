using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class NestedTryReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-try-" + Guid.NewGuid().ToString("N"));

    public NestedTryReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Try_FindsInnerCatchSwallowing()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        try", "        {",
            "            try", "            {", "                A();", "            }",
            "            catch (Exception)", "            {", "                B();", "            }",
            "        }", "        catch (Exception e)", "        {", "            Log(e);", "        }", "    }",
            "    void A() { }", "    void B() { }", "    void Log(object e) { }", "}");
        var result = await new NestedTryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("嵌套 try 报告: 1 个 .cs 文件", result);
        Assert.Contains("内层 catch 吞异常", result);
        Assert.Contains("静默消失", result);
    }

    [Fact]
    public async Task Try_ReThrowIsClean()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        try", "        {",
            "            try", "            {", "                A();", "            }",
            "            catch (Exception)", "            {", "                throw;", "            }",
            "        }", "        catch (Exception e)", "        {", "            Log(e);", "        }", "    }",
            "    void A() { }", "    void Log(object e) { }", "}");
        var result = await new NestedTryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("内层 catch 吞异常", result);
    }

    [Fact]
    public async Task Try_FindsReturnInFinally()
    {
        WriteSource(
            "class A", "{", "    int M()", "    {", "        try", "        {", "            return 1;",
            "        }", "        finally", "        {", "            return 2;", "        }", "    }", "}");
        var result = await new NestedTryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("finally 覆盖异常", result);
        Assert.Contains("覆盖", result);
    }

    [Fact]
    public async Task Try_PlainFinallyIsClean()
    {
        WriteSource(
            "class A", "{", "    int M()", "    {", "        try", "        {", "            return 1;",
            "        }", "        finally", "        {", "            Cleanup();", "        }", "    }", "    void Cleanup() { }", "}");
        var result = await new NestedTryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("finally 覆盖异常", result);
    }

    [Fact]
    public async Task Try_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new NestedTryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new NestedTryReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NestedTryReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
