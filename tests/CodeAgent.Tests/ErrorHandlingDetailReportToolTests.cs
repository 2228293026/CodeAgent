using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ErrorHandlingDetailReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-errdetail-" + Guid.NewGuid().ToString("N"));

    public ErrorHandlingDetailReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ErrorHandlingDetail_FindsSilentDefaultReturn()
    {
        WriteSource(
            "class A",
            "{",
            "    int M()",
            "    {",
            "        try { return Risky(); }",
            "        catch (Exception) { return 0; }",
            "    }",
            "}");
        var result = await new ErrorHandlingDetailReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("错误处理细节报告: 1 个 .cs 文件", result);
        Assert.Contains("静默返回默认值: 1", result);
        Assert.Contains("调用方无法区分失败", result);
    }

    [Fact]
    public async Task ErrorHandlingDetail_LoggedDefaultIsNotFlagged()
    {
        WriteSource(
            "class A",
            "{",
            "    int M()",
            "    {",
            "        try { return Risky(); }",
            "        catch (Exception e) { Log.Warn(e); return 0; }",
            "    }",
            "}");
        var result = await new ErrorHandlingDetailReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("静默返回默认值", result);
    }

    [Fact]
    public async Task ErrorHandlingDetail_FindsLostInnerException()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        try { Risky(); }",
            "        catch (Exception e) { throw new InvalidOperationException(\"失败\"); }",
            "    }",
            "}");
        var result = await new ErrorHandlingDetailReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("异常包装丢 inner: 1", result);
        Assert.Contains("原异常堆栈丢失", result);
    }

    [Fact]
    public async Task ErrorHandlingDetail_InnerExceptionIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        try { Risky(); }",
            "        catch (Exception e) { throw new InvalidOperationException(\"失败\", e); }",
            "    }",
            "}");
        var result = await new ErrorHandlingDetailReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("异常包装丢 inner", result);
    }

    [Fact]
    public async Task ErrorHandlingDetail_FindsUnobservedTask()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        _ = DoWorkAsync();", "    }", "}");
        var result = await new ErrorHandlingDetailReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未观察的 Task: 1", result);
        Assert.Contains("异常会在终结时抛出", result);
    }

    [Fact]
    public async Task ErrorHandlingDetail_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    const int N = 3;", "    int V => N;", "}");
        var result = await new ErrorHandlingDetailReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现错误处理细节问题", result);
    }

    [Fact]
    public async Task ErrorHandlingDetail_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ErrorHandlingDetailReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ErrorHandlingDetailReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ErrorHandlingDetailReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
