using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DuplicateCodeReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-dupcode-" + Guid.NewGuid().ToString("N"));

    public DuplicateCodeReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteSource(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    [Fact]
    public async Task DuplicateCodeReport_FindsRepeatedFragment()
    {
        const string body = "        var total = 0;\n        foreach (var x in items)\n            total += x.Value;";
        WriteSource("A.cs", "class A", "{", "    int F()", "    {", body, "        return total;", "    }", "}");
        WriteSource("B.cs", "class B", "{", "    int G()", "    {", body, "        return total;", "    }", "}");
        var result = await new DuplicateCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重复代码片段（3 行及以上）:", result);
        // 关键性质：有一行同时点名 A.cs 与 B.cs（滑窗会命中多个重叠窗口，不必逐个计数）
        Assert.Contains(result.Split('\n'),
            l => l.Contains("A.cs:") && l.Contains("B.cs:"));
    }

    [Fact]
    public async Task DuplicateCodeReport_IgnoresDifferentIndentation()
    {
        // 同逻辑不同缩进的拷贝粘贴同样算重复
        WriteSource("A.cs", "class A", "{", "    void F()", "    {", "        Run();", "        Log();", "        Done();", "    }", "}");
        WriteSource("B.cs", "class B", "{", "    void G()", "    {", "            Run();", "            Log();", "            Done();", "    }", "}");
        var result = await new DuplicateCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains(result.Split('\n'),
            l => l.Contains("A.cs:") && l.Contains("B.cs:"));
    }

    [Fact]
    public async Task DuplicateCodeReport_FindsRepeatedConstantName()
    {
        WriteSource("A.cs", "class A", "{", "    const int Limit = 10;", "}");
        WriteSource("B.cs", "class B", "{", "    const int Limit = 20;", "}");
        var result = await new DuplicateCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("同名常量重复定义: 1 处", result);
        Assert.Contains("Limit", result);
    }

    [Fact]
    public async Task DuplicateCodeReport_ShortFragmentsAreNotReported()
    {
        // 两行相同属于样板，不应报
        WriteSource("A.cs", "class A", "{", "    int X => 1;", "}");
        WriteSource("B.cs", "class B", "{", "    int Y => 1;", "}");
        var result = await new DuplicateCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重复代码片段（3 行及以上）: 0 处", result);
    }

    [Fact]
    public async Task DuplicateCodeReport_UniqueCodeHasNoDuplicates()
    {
        WriteSource("A.cs", "class A", "{", "    int F()", "    {", "        return Alpha();", "    }", "}");
        var result = await new DuplicateCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现重复代码", result);
    }

    [Fact]
    public async Task DuplicateCodeReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DuplicateCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DuplicateCodeReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("A.cs", "class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DuplicateCodeReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
