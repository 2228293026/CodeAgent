using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class PathEscapeReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-escape-" + Guid.NewGuid().ToString("N"));

    public PathEscapeReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Escape_FindsUnvalidatedCombine()
    {
        WriteSource("class A", "{", "    string M(string root, string rel)", "    {", "        return Path.Combine(root, rel);", "    }", "}");
        var result = await new PathEscapeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("路径逃逸报告: 1 个 .cs 文件", result);
        Assert.Contains("Combine 后未校验根目录", result);
    }

    [Fact]
    public async Task Escape_ValidatedCombineIsFine()
    {
        WriteSource(
            "class A", "{", "    string M(string root, string rel)", "    {",
            "        var p = Path.Combine(root, rel);",
            "        return Path.GetFullPath(p);", "    }", "}");
        var result = await new PathEscapeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("Combine 后未校验根目录", result);
    }

    [Fact]
    public async Task Escape_FindsStringPrefixCheck()
    {
        WriteSource("class A", "{", "    bool M(string p) => p.StartsWith(@\"D:\\work\");", "}");
        var result = await new PathEscapeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("用字符串前缀判断路径归属", result);
        Assert.Contains("../root2", result);
    }

    [Fact]
    public async Task Escape_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new PathEscapeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new PathEscapeReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PathEscapeReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
