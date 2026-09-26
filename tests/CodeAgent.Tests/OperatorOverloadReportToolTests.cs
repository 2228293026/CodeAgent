using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class OperatorOverloadReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-opover-" + Guid.NewGuid().ToString("N"));

    public OperatorOverloadReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Operator_FindsHalfPair()
    {
        WriteSource(
            "class A",
            "{",
            "    public static bool operator ==(A a, A b) => true;",
            "}");
        var result = await new OperatorOverloadReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("运算符重载报告: 1 个 .cs 文件", result);
        Assert.Contains("成对运算符只重载一半", result);
        Assert.Contains("== / !=", result);
    }

    [Fact]
    public async Task Operator_CompletePairIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    public static bool operator ==(A a, A b) => true;",
            "    public static bool operator !=(A a, A b) => false;",
            "    public override bool Equals(object? o) => true;",
            "    public override int GetHashCode() => 0;",
            "}");
        var result = await new OperatorOverloadReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("成对运算符只重载一半", result);
        Assert.DoesNotContain("相等语义不一致", result);
    }

    [Fact]
    public async Task Operator_FindsEqualsWithoutOperator()
    {
        WriteSource(
            "class A",
            "{",
            "    public override bool Equals(object? o) => true;",
            "    public override int GetHashCode() => 0;",
            "}");
        var result = await new OperatorOverloadReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("相等语义不一致", result);
        Assert.Contains("行为不一致", result);
    }

    [Fact]
    public async Task Operator_FindsOperatorWithoutGetHashCode()
    {
        WriteSource(
            "class A",
            "{",
            "    public static bool operator ==(A a, A b) => true;",
            "    public static bool operator !=(A a, A b) => false;",
            "}");
        var result = await new OperatorOverloadReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("相等语义不一致", result);
        Assert.Contains("GetHashCode", result);
    }

    [Fact]
    public async Task Operator_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new OperatorOverloadReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现运算符重载问题", result);
    }

    [Fact]
    public async Task Operator_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new OperatorOverloadReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new OperatorOverloadReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OperatorOverloadReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
