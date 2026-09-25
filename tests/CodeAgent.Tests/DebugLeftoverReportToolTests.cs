using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DebugLeftoverReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-debugleft-" + Guid.NewGuid().ToString("N"));

    public DebugLeftoverReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void Write(string relative, params string[] lines)
    {
        var file = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join("\n", lines));
    }

    [Fact]
    public async Task DebugLeftoverReport_FindsBreakpointAndReadKey()
    {
        Write("src/A.cs",
            "class A",
            "{",
            "    void M() { Debugger.Break(); }",
            "    void N() { Console.ReadKey(); }",
            "}");
        var result = await new DebugLeftoverReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("调试残留报告: 扫描 1 个文件", result);
        Assert.Contains("断点语句: 1", result);
        Assert.Contains("绝不能提交", result);
        Assert.Contains("等待按键: 1", result);
        Assert.Contains("永久阻塞", result);
        Assert.Contains("src/A.cs:3", result);
    }

    [Fact]
    public async Task DebugLeftoverReport_FindsBreakpointMidLine()
    {
        // 断点写在行中间（if 之后）与行首一样致命，不能因为 ^ 锚点而漏报
        Write("src/A.cs", "    void M(bool b) { if (b) Debugger.Break(); }");
        var result = await new DebugLeftoverReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("断点语句: 1", result);
    }

    [Fact]
    public async Task DebugLeftoverReport_FindsCommentedOutTestAndWriteHost()
    {
        Write("tests/B.cs", "// [Fact]", "public void T() { }");
        Write("scripts/c.ps1", "Write-Host 'hi'");
        var result = await new DebugLeftoverReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("临时测试跳过: 1", result);
        Assert.Contains("Write-Host/Console 混用: 1", result);
        Assert.Contains("tests/B.cs:1", result);
    }

    [Fact]
    public async Task DebugLeftoverReport_SignalFilterAndBadSignal()
    {
        Write("src/A.cs", "Debugger.Break();", "Console.ReadKey();");
        var args = new JsonObject { ["signals"] = new JsonArray("断点语句") };
        var result = await new DebugLeftoverReportTool().ExecuteAsync(args, Context(), CancellationToken.None);
        Assert.Contains("断点语句: 1", result);
        Assert.DoesNotContain("等待按键", result);
        await Assert.ThrowsAsync<ToolException>(() => new DebugLeftoverReportTool().ExecuteAsync(
            new JsonObject { ["signals"] = new JsonArray("NOPE") }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task DebugLeftoverReport_CleanCodeHasNoLeftovers()
    {
        Write("src/A.cs", "class A { int V => 42; }");
        var result = await new DebugLeftoverReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现调试残留", result);
    }

    [Fact]
    public async Task DebugLeftoverReport_RejectsNoSourceOutsideAndCancellation()
    {
        var none = await new DebugLeftoverReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs/.ps1 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DebugLeftoverReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        Write("src/A.cs", "class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DebugLeftoverReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
