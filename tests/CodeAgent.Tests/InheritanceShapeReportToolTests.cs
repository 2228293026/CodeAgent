using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class InheritanceShapeReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-inherit-" + Guid.NewGuid().ToString("N"));

    public InheritanceShapeReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Inheritance_FindsUninitializedNrtField()
    {
        WriteSource("class A", "{", "    private string _name;", "}");
        var result = await new InheritanceShapeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("继承体系报告: 1 个 .cs 文件", result);
        Assert.Contains("非空字段未初始化", result);
        Assert.Contains("CS8618", result);
    }

    [Fact]
    public async Task Inheritance_AssignedFieldIsFine()
    {
        WriteSource("class A", "{", "    private string _name = \"x\";", "}");
        var result = await new InheritanceShapeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("非空字段未初始化", result);
    }

    [Fact]
    public async Task Inheritance_FindsNewHiding()
    {
        WriteSource(
            "class A",
            "{",
            "    public new string Name() => \"a\";",
            "}");
        var result = await new InheritanceShapeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("new 隐藏成员", result);
        Assert.Contains("多态", result);
    }

    [Fact]
    public async Task Inheritance_OverrideIsFine()
    {
        WriteSource("class A", "{", "    public override string Name() => \"a\";", "}");
        var result = await new InheritanceShapeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("new 隐藏成员", result);
        Assert.DoesNotContain("成员隐藏", result);
    }

    [Fact]
    public async Task Inheritance_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new InheritanceShapeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现继承体系问题", result);
    }

    [Fact]
    public async Task Inheritance_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new InheritanceShapeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new InheritanceShapeReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InheritanceShapeReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
