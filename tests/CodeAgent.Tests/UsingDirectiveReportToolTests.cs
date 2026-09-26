using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class UsingDirectiveReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-using-" + Guid.NewGuid().ToString("N"));

    public UsingDirectiveReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Using_FindsUnusedNamespace()
    {
        WriteSource("using System.Text;", "using Never.Used.At.All;", "", "class A", "{", "    int V => 3;", "}");
        var result = await new UsingDirectiveReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("using 指令报告: 1 个 .cs 文件", result);
        Assert.Contains("多余 using", result);
        Assert.Contains("Never.Used.At.All", result);
    }

    [Fact]
    public async Task Using_UsedNamespaceIsFine()
    {
        // 命名空间被使用时，正文里出现的是**最后一段**命名的类型
        WriteSource("using Never.Used.At.All;", "", "class A", "{", "    All Thing = new All();", "}");
        var result = await new UsingDirectiveReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("Never.Used.At.All", result);
    }

    [Fact]
    public async Task Using_FlagsImplicitSystemUsings()
    {
        WriteSource(
            "using System;",
            "using System.IO;",
            "using System.Linq;",
            "using System.Text;",
            "using System.Threading;",
            "using System.Threading.Tasks;",
            "",
            "class A",
            "{",
            "    int V => 3;",
            "}");
        var result = await new UsingDirectiveReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("隐式 using 已覆盖", result);
        Assert.Contains("System.* 过多", result);
    }

    [Fact]
    public async Task Using_FileWithoutUsingsHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new UsingDirectiveReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 using 指令问题", result);
    }

    [Fact]
    public async Task Using_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new UsingDirectiveReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new UsingDirectiveReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UsingDirectiveReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
