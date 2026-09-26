using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FileSafetyReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-filesafety-" + Guid.NewGuid().ToString("N"));

    public FileSafetyReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FileSafetyReport_FindsUnguardedDelete()
    {
        WriteSource("class A", "{", "    void M(string p) => File.Delete(p);", "}");
        var result = await new FileSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("文件安全报告: 1 个 .cs 文件", result);
        Assert.Contains("删除未校验: 1", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task FileSafetyReport_GuardedDeleteIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(string p)",
            "    {",
            "        if (File.Exists(p)) File.Delete(p);",
            "    }",
            "}");
        var result = await new FileSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("删除未校验", result);
    }

    [Fact]
    public async Task FileSafetyReport_FindsPathConcatenation()
    {
        WriteSource(
            "class A",
            "{",
            "    string M(string root, string name) => Path + \"/\" + name;",
            "}");
        var result = await new FileSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("路径字符串拼接: 1", result);
        Assert.Contains("Path.Combine", result);
    }

    [Fact]
    public async Task FileSafetyReport_PathCombineIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    string M(string root, string name) => Path.Combine(root, name);",
            "}");
        var result = await new FileSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("路径字符串拼接", result);
    }

    [Fact]
    public async Task FileSafetyReport_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    const int N = 3;", "    int V => N;", "}");
        var result = await new FileSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现文件操作安全问题", result);
    }

    [Fact]
    public async Task FileSafetyReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new FileSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new FileSafetyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileSafetyReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
