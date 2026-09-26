using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class PartialMethodReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-partial-" + Guid.NewGuid().ToString("N"));

    public PartialMethodReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void Write(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    [Fact]
    public async Task Partial_FindsUnimplementedMethod()
    {
        Write("A.cs", "partial class A", "{", "    partial void Hook(int n);", "}");
        var result = await new PartialMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("partial 成员报告: 1 个 .cs 文件", result);
        Assert.Contains("partial 方法未实现", result);
        Assert.Contains("Hook", result);
    }

    [Fact]
    public async Task Partial_ImplementedMethodIsFine()
    {
        Write("A.cs", "partial class A", "{", "    partial void Hook(int n) { }", "}");
        var result = await new PartialMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("partial 方法未实现", result);
    }

    [Fact]
    public async Task Partial_FindsMissingModifier()
    {
        Write("A.cs", "class A", "{", "}");
        Write("B.cs", "partial class A", "{", "}");
        var result = await new PartialMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("partial 只写一半", result);
        Assert.Contains("重复定义", result);
    }

    [Fact]
    public async Task Partial_FindsCrossFileType()
    {
        Write("A.cs", "partial class Big", "{", "}");
        Write("B.cs", "partial class Big", "{", "}");
        var result = await new PartialMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("类型跨文件拆分", result);
        Assert.Contains("Big", result);
    }

    [Fact]
    public async Task Partial_CleanCodeHasNoIssues()
    {
        Write("A.cs", "class A", "{", "    int V => 3;", "}");
        var result = await new PartialMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 partial 成员问题", result);
    }

    [Fact]
    public async Task Partial_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new PartialMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new PartialMethodReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        Write("A.cs", "class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PartialMethodReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
