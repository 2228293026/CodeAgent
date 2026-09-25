using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ExceptionHandlingReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-exch-" + Guid.NewGuid().ToString("N"));

    public ExceptionHandlingReportToolTests() => Directory.CreateDirectory(_dir);

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

    [Theory]
    [InlineData("Exception", true)]
    [InlineData("System.Exception", true)]
    [InlineData("object", true)]
    [InlineData("IOException", false)]
    [InlineData("OperationCanceledException", false)]
    public void IsBroadType_IdentifiesOverBroadCatches(string type, bool expected) =>
        Assert.Equal(expected, ExceptionHandlingReportTool.IsBroadType(type));

    [Fact]
    public void BodyOf_IgnoresBlankLinesAndComments()
    {
        var body = ExceptionHandlingReportTool.BodyOf(
            ["catch (Exception ex)", "{", "    // 说明", "", "    Log(ex);", "}"], 0);
        Assert.Contains("Log(ex);", body);
        Assert.DoesNotContain("// 说明", body);
    }

    [Fact]
    public async Task ExceptionHandlingReport_FindsEmptyAndBroadCatch()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        try { Risky(); }",
            "        catch (Exception) { }",
            "        try { Risky(); }",
            "        catch (Exception ex) { Log(ex); }",
            "    }",
            "}");
        var result = await new ExceptionHandlingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        // 只有一个 catch 真正为空：单行 catch (Exception ex) { Log(ex); } 有内容，不该误报
        Assert.Contains("空 catch（异常被吞掉）: 1", result);
        Assert.Contains("异常被完全吞掉", result);
        Assert.Contains("捕获过宽: 2", result);
        Assert.Contains("A.cs:6", result); // 第 5 行是 try，空 catch 在第 6 行
    }

    [Fact]
    public async Task ExceptionHandlingReport_SingleLineCatchWithBodyIsNotEmpty()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        try { Risky(); }",
            "        catch (IOException e) { Log(e.Message); }",
            "    }",
            "}");
        var result = await new ExceptionHandlingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("空 catch", result);
        Assert.DoesNotContain("捕获变量未使用", result);
    }

    [Fact]
    public async Task ExceptionHandlingReport_FindsUnusedVariable()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        try { Risky(); }",
            "        catch (IOException e) { }",
            "    }",
            "}");
        var result = await new ExceptionHandlingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("捕获变量未使用: 1", result);
        Assert.Contains("e 从未被使用", result);
    }

    [Fact]
    public async Task ExceptionHandlingReport_UsedVariableIsNotFlagged()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        try { Risky(); }",
            "        catch (IOException e) { Log(e.Message); }",
            "    }",
            "}");
        var result = await new ExceptionHandlingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("异常处理报告: 1 个文件，1 个 catch", result);
        Assert.DoesNotContain("捕获变量未使用", result);
        Assert.DoesNotContain("空 catch", result);
    }

    [Fact]
    public async Task ExceptionHandlingReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ExceptionHandlingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ExceptionHandlingReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExceptionHandlingReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
