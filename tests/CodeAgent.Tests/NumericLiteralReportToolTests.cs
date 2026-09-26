using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class NumericLiteralReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-numlit-" + Guid.NewGuid().ToString("N"));

    public NumericLiteralReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Numeric_FindsMagicNumber()
    {
        WriteSource("class A", "{", "    bool M(int n) => n > 10000;", "}");
        var result = await new NumericLiteralReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("数值字面量报告: 1 个 .cs 文件", result);
        Assert.Contains("魔法数字", result);
        Assert.Contains("10000", result);
    }

    [Fact]
    public async Task Numeric_SmallNumbersAreNotMagic()
    {
        WriteSource("class A", "{", "    bool M(int n) => n > 5;", "}");
        var result = await new NumericLiteralReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("魔法数字", result);
    }

    [Fact]
    public async Task Numeric_FindsFloatEquality()
    {
        WriteSource(
            "class A",
            "{",
            "    bool M(double a)",
            "    {",
            "        return a == 0.1;",
            "    }",
            "}");
        var result = await new NumericLiteralReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("浮点相等比较", result);
        Assert.Contains("容差", result);
    }

    [Fact]
    public async Task Numeric_FindsUnitMixup()
    {
        // 真实写法：按字符长度分配字节缓冲区 —— .Length 是字符数，byte[] 要的是字节数
        WriteSource(
            "class A",
            "{",
            "    byte[] M(string s)",
            "    {",
            "        var byteBuffer = new byte[s.Length * 4]; // UTF-8 最多 4 字节/字符",
            "        return byteBuffer;",
            "    }",
            "}");
        var result = await new NumericLiteralReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("字节/字符混用", result);
    }

    [Fact]
    public async Task Numeric_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new NumericLiteralReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现数值字面量问题", result);
    }

    [Fact]
    public async Task Numeric_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new NumericLiteralReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new NumericLiteralReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NumericLiteralReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
