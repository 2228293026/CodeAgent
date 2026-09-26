using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LoggingPracticeReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-logging-" + Guid.NewGuid().ToString("N"));

    public LoggingPracticeReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteSource(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    [Fact]
    public async Task LoggingPracticeReport_FindsConsoleWriteInLibrary()
    {
        WriteSource("Service.cs", "class S", "{", "    void M() => Console.WriteLine(\"x\");", "}");
        var result = await new LoggingPracticeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("日志规范报告: 1 个 .cs 文件", result);
        Assert.Contains("库代码写控制台: 1", result);
        Assert.Contains("应走日志抽象", result);
    }

    [Fact]
    public async Task LoggingPracticeReport_EntryPointConsoleIsAllowed()
    {
        // Program.cs 是 CLI 入口，直接写控制台正是它该做的
        WriteSource("Program.cs", "class P", "{", "    void M() => Console.WriteLine(\"x\");", "}");
        var result = await new LoggingPracticeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("库代码写控制台", result);
    }

    [Fact]
    public async Task LoggingPracticeReport_FindsMessageOnlyLogging()
    {
        WriteSource("S.cs", "class S", "{", "    void M(Exception ex)", "    {", "        Log.Warn(ex.Message);", "    }", "}");
        var result = await new LoggingPracticeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("只取 Message: 1", result);
        Assert.Contains("丢失堆栈", result);
    }

    [Fact]
    public async Task LoggingPracticeReport_FullExceptionIsFine()
    {
        WriteSource("S.cs", "class S", "{", "    void M(Exception ex)", "    {", "        Log.Error(ex.ToString());", "    }", "}");
        var result = await new LoggingPracticeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现日志规范问题", result);
    }

    [Fact]
    public async Task LoggingPracticeReport_FindsSwallowingCatch()
    {
        WriteSource("S.cs", "class S", "{", "    void M()", "    {", "        try { Risky(); }", "        catch (Exception ex)", "        {", "            Log.Error(ex.Message);", "        }", "    }", "}");
        var result = await new LoggingPracticeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("catch 吞异常: 1", result);
    }

    [Fact]
    public async Task LoggingPracticeReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new LoggingPracticeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new LoggingPracticeReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("S.cs", "class S { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LoggingPracticeReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
