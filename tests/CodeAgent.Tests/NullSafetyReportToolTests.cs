using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class NullSafetyReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-nullsafety-" + Guid.NewGuid().ToString("N"));

    public NullSafetyReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task NullSafetyReport_FindsUncheckedIndex()
    {
        WriteSource(
            "class A",
            "{",
            "    int M(Dictionary<string, int> map)",
            "    {",
            "        return map[\"k\"].ToString().Length;",
            "    }",
            "}");
        var result = await new NullSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("空值安全报告: 1 个 .cs 文件", result);
        Assert.Contains("下标未判存在: 1", result);
        Assert.Contains("键不存在时会 NRE", result);
    }

    [Fact]
    public async Task NullSafetyReport_AcceptsTryGetValue()
    {
        WriteSource(
            "class A",
            "{",
            "    int M(Dictionary<string, int> map)",
            "    {",
            "        map.TryGetValue(\"k\", out var v);",
            "        return v.ToString().Length;",
            "    }",
            "}");
        var result = await new NullSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("下标未判存在", result);
    }

    [Fact]
    public async Task NullSafetyReport_FindsOrDefaultChaining()
    {
        WriteSource(
            "class A",
            "{",
            "    int M(List<string> xs)",
            "    {",
            "        return xs.FirstOrDefault().Length;",
            "    }",
            "}");
        var result = await new NullSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("OrDefault 直接链式: 1", result);
        Assert.Contains("可能为 null", result);
    }

    [Fact]
    public async Task NullSafetyReport_FindsMissingCoalesce()
    {
        WriteSource(
            "class A",
            "{",
            "    string M(bool f)",
            "    {",
            "        var s = f ? \"a\" : null;",
            "        return s;",
            "    }",
            "}");
        var result = await new NullSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("缺 ?? 兜底: 1", result);
    }

    [Fact]
    public async Task NullSafetyReport_CleanCodeHasNoIssues()
    {
        WriteSource(
            "class A",
            "{",
            "    string M(bool f)",
            "    {",
            "        var s = f ? \"a\" : \"\";",
            "        return s ?? \"\";",
            "    }",
            "}");
        var result = await new NullSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现空值安全问题", result);
    }

    [Fact]
    public async Task NullSafetyReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new NullSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new NullSafetyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NullSafetyReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
