using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DuplicateLogicReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-dup-" + Guid.NewGuid().ToString("N"));

    public DuplicateLogicReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Dup_FindsSameLiteralUnderTwoNames()
    {
        WriteSource(
            "class A", "{",
            "    const string TimeoutMsg = \"操作已超时，请稍后重试\";",
            "    const string DeadMsg = \"操作已超时，请稍后重试\";",
            "}");
        var result = await new DuplicateLogicReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重复逻辑报告: 1 个 .cs 文件", result);
        Assert.Contains("同一字面量多个常量名", result);
        Assert.Contains("TimeoutMsg", result);
        Assert.Contains("DeadMsg", result);
    }

    [Fact]
    public async Task Dup_FindsCopyPastedBodies()
    {
        // 关键：只改了函数名，方法体一模一样——这是最难人工发现的复制粘贴
        WriteSource(
            "class A", "{",
            "    int Alpha(int x)", "    {", "        var y = x * 2 + 1;", "        var z = y - 3;", "        return y + z;", "    }",
            "    int Beta(int x)", "    {", "        var y = x * 2 + 1;", "        var z = y - 3;", "        return y + z;", "    }", "}");
        var result = await new DuplicateLogicReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("复制粘贴的方法体", result);
        Assert.Contains("Alpha", result);
        Assert.Contains("Beta", result);
    }

    [Fact]
    public async Task Dup_FindsMagicNumber()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        var a = 24000;", "        var b = 24000;", "        var c = 24000;", "    }", "}");
        var result = await new DuplicateLogicReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("散落的魔法数字", result);
        Assert.Contains("24000", result);
    }

    [Fact]
    public async Task Dup_ShortAndDistinctCodeIsFine()
    {
        WriteSource(
            "class A", "{",
            "    int Alpha(int x) => x * 2;",
            "    int Beta(int x) => x * 3;",
            "    const string A = \"a-very-long-distinct-value\";",
            "    const string B = \"another-very-long-value-here\";",
            "}");
        var result = await new DuplicateLogicReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现重复逻辑", result);
    }

    [Fact]
    public async Task Dup_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DuplicateLogicReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DuplicateLogicReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DuplicateLogicReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
