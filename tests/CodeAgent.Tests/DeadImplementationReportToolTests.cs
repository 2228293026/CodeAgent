using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DeadImplementationReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-deadimpl-" + Guid.NewGuid().ToString("N"));

    public DeadImplementationReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task DeadImplementationReport_FindsNotImplemented()
    {
        WriteSource(
            "class A",
            "{",
            "    public void Do()",
            "    {",
            "        throw new NotImplementedException();",
            "    }",
            "}");
        var result = await new DeadImplementationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未实现: 1", result);
        Assert.Contains("Do()", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task DeadImplementationReport_FindsConstantReturn()
    {
        WriteSource(
            "class A",
            "{",
            "    public string Name()",
            "    {",
            "        return null;",
            "    }",
            "}");
        var result = await new DeadImplementationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("恒返回常量: 1", result);
        Assert.Contains("未实现的桩", result);
    }

    [Fact]
    public async Task DeadImplementationReport_FindsBaseOnlyOverride()
    {
        WriteSource(
            "class A : B",
            "{",
            "    public override void Run()",
            "    {",
            "        base.Run();",
            "    }",
            "}");
        var result = await new DeadImplementationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("只调用 base: 1", result);
        Assert.Contains("空转发", result);
    }

    [Fact]
    public async Task DeadImplementationReport_RealImplementationNotFlagged()
    {
        WriteSource(
            "class A",
            "{",
            "    public int Add(int a, int b)",
            "    {",
            "        return a + b;",
            "    }",
            "}");
        var result = await new DeadImplementationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现空实现", result);
    }

    [Fact]
    public async Task DeadImplementationReport_PropertiesAreNotMethods()
    {
        WriteSource(
            "class A",
            "{",
            "    public string Name { get; set; }",
            "    public int Count => 3;",
            "}");
        var result = await new DeadImplementationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("恒返回常量", result);
        Assert.Contains("0 个方法", result);
    }

    [Fact]
    public async Task DeadImplementationReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DeadImplementationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DeadImplementationReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeadImplementationReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
