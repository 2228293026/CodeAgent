using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ArrayBoundsReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-arr-" + Guid.NewGuid().ToString("N"));

    public ArrayBoundsReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Arr_FindsIndexEqualToLength()
    {
        WriteSource("class A", "{", "    int M(int[] arr)", "    {", "        return arr[arr.Length];", "    }", "}");
        var result = await new ArrayBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("数组越界报告: 1 个 .cs 文件", result);
        Assert.Contains("off-by-one 上界", result);
        Assert.Contains("Length-1", result);
    }

    [Fact]
    public async Task Arr_FindsSubtractionWithoutGuard()
    {
        WriteSource("class A", "{", "    int M(int[] arr, int i)", "    {", "        return arr[i - 1];", "    }", "}");
        var result = await new ArrayBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("减法索引无下界保护", result);
        Assert.Contains("IndexOutOfRangeException", result);
    }

    [Fact]
    public async Task Arr_GuardedSubtractionIsClean()
    {
        WriteSource(
            "class A", "{", "    int M(int[] arr, int i)", "    {",
            "        if (i > 0)", "        {", "            return arr[i - 1];", "        }", "        return 0;", "    }", "}");
        var result = await new ArrayBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("减法索引无下界保护", result);
    }

    [Fact]
    public async Task Arr_FindsSplitResultIndexed()
    {
        WriteSource("class A", "{", "    string M(string s)", "    {", "        return s.Split(',')[0];", "    }", "}");
        var result = await new ArrayBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("可能为空的结果直接取下标", result);
    }

    [Fact]
    public async Task Arr_SimpleIndexIsClean()
    {
        WriteSource("class A", "{", "    int M(int[] arr, int i)", "    {", "        return arr[i];", "    }", "}");
        var result = await new ArrayBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现数组越界问题", result);
    }

    [Fact]
    public async Task Arr_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ArrayBoundsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ArrayBoundsReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArrayBoundsReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
