using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class EnumUsageReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-enum-" + Guid.NewGuid().ToString("N"));

    public EnumUsageReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Enum_FindsSwitchWithoutDefault()
    {
        WriteSource(
            "enum Level { Low, High }",
            "class A",
            "{",
            "    int M(Level l)",
            "    {",
            "        switch (l)",
            "        {",
            "            case Level.Low: return 1;",
            "        }",
            "        return 0;",
            "    }",
            "}");
        var result = await new EnumUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("枚举使用报告: 1 个 .cs 文件", result);
        Assert.Contains("switch 缺 default", result);
        Assert.Contains("静默漏掉", result);
    }

    [Fact]
    public async Task Enum_SwitchWithDefaultIsFine()
    {
        WriteSource(
            "enum Level { Low, High }",
            "class A",
            "{",
            "    int M(Level l)",
            "    {",
            "        switch (l)",
            "        {",
            "            case Level.Low: return 1;",
            "            default: return 0;",
            "        }",
            "    }",
            "}");
        var result = await new EnumUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("switch 缺 default", result);
    }

    [Fact]
    public async Task Enum_FindsBitwiseWithoutFlags()
    {
        WriteSource(
            "enum Perm { Read, Write }",
            "class A",
            "{",
            "    void M(Perm a, Perm b) { var c = a | b; }",
            "}");
        var result = await new EnumUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("位运算缺 [Flags]", result);
    }

    [Fact]
    public async Task Enum_FlagsEnumIsFine()
    {
        WriteSource(
            "[System.Flags]",
            "enum Perm { Read, Write }",
            "class A",
            "{",
            "    void M(Perm a, Perm b) { var c = a | b; }",
            "}");
        var result = await new EnumUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("位运算缺 [Flags]", result);
    }

    [Fact]
    public async Task Enum_FindsOrderedComparison()
    {
        WriteSource(
            "enum Level { Low, High }",
            "class A",
            "{",
            "    bool M(Level a, Level b) => a > b;",
            "}");
        var result = await new EnumUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("枚举比大小", result);
        Assert.Contains("顺序没有语义", result);
    }

    [Fact]
    public async Task Enum_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new EnumUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现枚举使用问题", result);
    }

    [Fact]
    public async Task Enum_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new EnumUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new EnumUsageReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EnumUsageReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
