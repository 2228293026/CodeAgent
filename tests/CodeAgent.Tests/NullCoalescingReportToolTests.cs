using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class NullCoalescingReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-nullco-" + Guid.NewGuid().ToString("N"));

    public NullCoalescingReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task NullCoalescing_FindsLongChain()
    {
        WriteSource("class A", "{", "    string M(string? a, string? b, string? c) => a ?? b ?? c ?? \"\";", "}");
        var result = await new NullCoalescingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("空值处理报告: 1 个 .cs 文件", result);
        Assert.Contains("过长 ?? 链", result);
        Assert.Contains("模式匹配", result);
    }

    [Fact]
    public async Task NullCoalescing_ShortChainIsFine()
    {
        WriteSource("class A", "{", "    string M(string? a, string? b) => a ?? b ?? \"\";", "}");
        var result = await new NullCoalescingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("过长 ?? 链", result);
    }

    [Fact]
    public async Task NullCoalescing_FindsRepeatedDereference()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(string? s)",
            "    {",
            "        if (s is null)",
            "        {",
            "            return;",
            "        }",
            "        var a = s.Length;",
            "        var b = s.Length;",
            "    }",
            "}");
        var result = await new NullCoalescingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("判空后反复解引用", result);
    }

    [Fact]
    public async Task NullCoalescing_PatternMatchIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    int M(string? s)",
            "    {",
            "        if (s is null) return 0;",
            "        return s.Length;",
            "    }",
            "}");
        var result = await new NullCoalescingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("判空后反复解引用", result);
    }

    [Fact]
    public async Task NullCoalescing_FindsConsecutiveNullChecks()
    {
        WriteSource(
            "class A",
            "{",
            "    int M(string? a, string? b, string? c)",
            "    {",
            "        if (a != null && a.Length > 0) return 1;",
            "        else if (b != null && b.Length > 0) return 2;",
            "        else if (c != null) return 3;",
            "        return 0;",
            "    }",
            "}");
        var result = await new NullCoalescingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("连续判空", result);
    }

    [Fact]
    public async Task NullCoalescing_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new NullCoalescingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现空值处理问题", result);
    }

    [Fact]
    public async Task NullCoalescing_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new NullCoalescingReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new NullCoalescingReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NullCoalescingReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
