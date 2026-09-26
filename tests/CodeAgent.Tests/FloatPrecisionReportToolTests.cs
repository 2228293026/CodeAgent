using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FloatPrecisionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-float-" + Guid.NewGuid().ToString("N"));

    public FloatPrecisionReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Fp_FindsFloatLiteralInArithmetic()
    {
        WriteSource("class A", "{", "    decimal M(decimal total)", "    {", "        return total * 1.1;", "    }", "}");
        var result = await new FloatPrecisionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("浮点精度报告: 1 个 .cs 文件", result);
        Assert.Contains("浮点字面量参与算术", result);
    }

    [Fact]
    public async Task Fp_IntegerArithmeticIsClean()
    {
        WriteSource("class A", "{", "    int M(int total)", "    {", "        return total * 2;", "    }", "}");
        var result = await new FloatPrecisionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("浮点字面量参与算术", result);
    }

    [Fact]
    public async Task Fp_FindsFloatEquality()
    {
        WriteSource(
            "class A", "{", "    double a = 0;", "    double b = 0;",
            "    bool M()", "    {", "        if (a == b) return true;", "        return false;", "    }", "}");
        var result = await new FloatPrecisionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("浮点用 == 比较", result);
    }

    [Fact]
    public async Task Fp_StringEqualityIsNotFloatEquality()
    {
        // 关键误报防线：name == "x" 是字符串比较，和浮点无关
        WriteSource("class A", "{", "    bool M(string name)", "    {", "        if (name == \"x\") return true;", "        return false;", "    }", "}");
        var result = await new FloatPrecisionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("浮点用 == 比较", result);
    }

    [Fact]
    public async Task Fp_FindsRoundWithoutMode()
    {
        WriteSource("class A", "{", "    double M(double x)", "    {", "        return Math.Round(x, 2);", "    }", "}");
        var result = await new FloatPrecisionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Math.Round 未指定舍入模式", result);
    }

    [Fact]
    public async Task Fp_RoundWithModeIsClean()
    {
        WriteSource(
            "using System;", "class A", "{", "    decimal M(decimal x)", "    {",
            "        return Math.Round(x, 2, MidpointRounding.AwayFromZero);", "    }", "}");
        var result = await new FloatPrecisionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("Math.Round 未指定舍入模式", result);
    }

    [Fact]
    public void TheFloatLiteralPatternIgnoresIntegers()
    {
        Assert.Matches(FloatPrecisionReportTool.FloatLiteral, "total * 1.1");
        Assert.Matches(FloatPrecisionReportTool.FloatLiteral, "0.085");
        Assert.DoesNotMatch(FloatPrecisionReportTool.FloatLiteral, "total * 2");
        Assert.DoesNotMatch(FloatPrecisionReportTool.FloatLiteral, "v1.0_beta");
    }

    [Fact]
    public async Task Fp_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new FloatPrecisionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new FloatPrecisionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FloatPrecisionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
