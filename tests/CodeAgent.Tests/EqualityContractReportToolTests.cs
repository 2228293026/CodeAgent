using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class EqualityContractReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-eqcontr-" + Guid.NewGuid().ToString("N"));

    public EqualityContractReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Equality_FindsEqualsWithoutGetHashCode()
    {
        WriteSource(
            "class Point",
            "{",
            "    public override bool Equals(object? o) => o is Point p && p.X == X;",
            "}");
        var result = await new EqualityContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("相等性契约报告: 1 个 .cs 文件", result);
        Assert.Contains("Equals 缺 GetHashCode", result);
        Assert.Contains("HashSet", result);
    }

    [Fact]
    public async Task Equality_EqualsWithGetHashCodeIsFine()
    {
        WriteSource(
            "class Point",
            "{",
            "    public override bool Equals(object? o) => o is Point p && p.X == X;",
            "    public override int GetHashCode() => X.GetHashCode();",
            "}");
        var result = await new EqualityContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("Equals 缺 GetHashCode", result);
    }

    [Fact]
    public async Task Equality_FindsOperatorOnCustomType()
    {
        WriteSource(
            "class Point",
            "{",
            "    public override bool Equals(object? o) => o is Point p && p.X == X;",
            "    public override int GetHashCode() => X.GetHashCode();",
            "",
            "    public bool Same(Point a, Point b) => a == b;",
            "}");
        var result = await new EqualityContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("== 与 Equals 混用", result);
        Assert.Contains("语义可能不一致", result);
    }

    [Fact]
    public async Task Equality_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new EqualityContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现相等性契约问题", result);
    }

    [Fact]
    public async Task Equality_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new EqualityContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new EqualityContractReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EqualityContractReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
