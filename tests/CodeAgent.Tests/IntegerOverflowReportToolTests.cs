using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class IntegerOverflowReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-ovf-" + Guid.NewGuid().ToString("N"));

    public IntegerOverflowReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Ovf_FindsUncheckedArithmetic()
    {
        WriteSource("class A", "{", "    int M(int a, int b)", "    {", "        unchecked", "        {", "            int c = a + b;", "        }", "        return 0;", "    }", "}");
        var result = await new IntegerOverflowReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("整数溢出报告: 1 个 .cs 文件", result);
        Assert.Contains("unchecked 下的整数运算", result);
        Assert.Contains("静默回绕", result);
    }

    [Fact]
    public async Task Ovf_FindsTruncatingDivision()
    {
        WriteSource("class A", "{", "    int M(int a, int b)", "    {", "        int c = a / b;", "        return c;", "    }", "}");
        var result = await new IntegerOverflowReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("整数除法向零截断", result);
        Assert.Contains("-7/2", result);
    }

    [Fact]
    public async Task Ovf_FindsNarrowingCast()
    {
        WriteSource("class A", "{", "    byte M(int n)", "    {", "        byte v = (byte)n;", "        return v;", "    }", "}");
        var result = await new IntegerOverflowReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("窄化强制转换", result);
        Assert.Contains("静默截断", result);
    }

    [Fact]
    public async Task Ovf_DoubleArithmeticIsClean()
    {
        // double 运算不溢出（变 inf），不该被当成整数问题
        WriteSource("class A", "{", "    double M(double a, double b)", "    {", "        double c = a + b;", "        return c;", "    }", "}");
        var result = await new IntegerOverflowReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现整数溢出问题", result);
    }

    [Fact]
    public void TheNarrowTypeListIsCurated()
    {
        Assert.Contains("byte", IntegerOverflowReportTool.NarrowTypes);
        Assert.Contains("short", IntegerOverflowReportTool.NarrowTypes);
        // int 和 long 是"宽"的一侧，不是窄化目标
        Assert.DoesNotContain("int", IntegerOverflowReportTool.NarrowTypes);
        Assert.Contains("long", IntegerOverflowReportTool.WideTypes);
    }

    [Fact]
    public async Task Ovf_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new IntegerOverflowReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new IntegerOverflowReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IntegerOverflowReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
