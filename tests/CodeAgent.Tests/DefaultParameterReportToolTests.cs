using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DefaultParameterReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-dp-" + Guid.NewGuid().ToString("N"));

    public DefaultParameterReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Dp_FindsSharedMutableDefault()
    {
        WriteSource(
            "using System.Collections.Generic;", "class A", "{",
            "    public void M(List<int> items = new List<int>())", "    {", "    }", "}");
        var result = await new DefaultParameterReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("默认参数报告: 1 个 .cs 文件", result);
        Assert.Contains("默认值是共享的可变对象", result);
        Assert.Contains("同一个实例", result);
    }

    [Fact]
    public async Task Dp_FindsDateTimeNowDefault()
    {
        WriteSource(
            "using System;", "class A", "{",
            "    public void M(DateTime at = DateTime.Now)", "    {", "    }", "}");
        var result = await new DefaultParameterReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("默认值每次调用都不同", result);
    }

    [Fact]
    public async Task Dp_FindsGuidDefault()
    {
        WriteSource(
            "using System;", "class A", "{",
            "    public void M(Guid id = Guid.NewGuid())", "    {", "    }", "}");
        var result = await new DefaultParameterReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("默认值每次调用都不同", result);
    }

    [Fact]
    public async Task Dp_ConstantDefaultsAreClean()
    {
        WriteSource(
            "using System.Collections.Generic;", "class A", "{",
            "    public void M(int retries = 3, string name = null, bool flag = false, string tag = \"x\")",
            "    {", "    }", "}");
        var result = await new DefaultParameterReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现默认参数问题", result);
    }

    [Fact]
    public void SplitParamsKeepsGenericCommas()
    {
        // 默认值里的泛型逗号不是参数分隔符
        var parts = DefaultParameterReportTool.SplitParams("List<int> items = new List<int>(), int n = 1, string s = null");
        Assert.Equal(3, parts.Count);
        Assert.Contains("new List<int>()", parts[0]);
    }

    [Fact]
    public void TheSafeDefaultsListIsMinimal()
    {
        // 只是文档化的锚点：这三类确实是编译期常量
        Assert.Contains("null", DefaultParameterReportTool.SafeDefaults);
        Assert.Contains("true", DefaultParameterReportTool.SafeDefaults);
    }

    [Fact]
    public async Task Dp_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DefaultParameterReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DefaultParameterReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DefaultParameterReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
