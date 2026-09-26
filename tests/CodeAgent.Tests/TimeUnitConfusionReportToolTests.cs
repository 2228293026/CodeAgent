using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TimeUnitConfusionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-time-" + Guid.NewGuid().ToString("N"));

    public TimeUnitConfusionReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Time_FindsMsNamedSeconds()
    {
        WriteSource("class A", "{", "    private const int RequestTimeoutMs = 30;", "}");
        var result = await new TimeUnitConfusionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("时间单位报告: 1 个 .cs 文件", result);
        Assert.Contains("名字说毫秒、值像秒", result);
        Assert.Contains("RequestTimeoutMs", result);
    }

    [Fact]
    public async Task Time_FindsSecondsNamedMillis()
    {
        WriteSource("class A", "{", "    private const int RetryIntervalSeconds = 30000;", "}");
        var result = await new TimeUnitConfusionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("名字说秒、值像毫秒", result);
        Assert.Contains("RetryIntervalSeconds", result);
    }

    [Fact]
    public async Task Time_FindsUnitlessTimeout()
    {
        WriteSource("class A", "{", "    private int Timeout = 5000;", "}");
        var result = await new TimeUnitConfusionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("超时值没有单位", result);
        Assert.Contains("无从判断", result);
    }

    [Fact]
    public async Task Time_TimeSpanSecondsWithMsNaming()
    {
        WriteSource("class A", "{", "    int T => (int)TimeSpan.FromSeconds(30).TotalMilliseconds; // RequestTimeoutMs", "}");
        var result = await new TimeUnitConfusionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("名字说毫秒、值像秒", result);
    }

    [Fact]
    public async Task Time_ConsistentValuesAreFine()
    {
        WriteSource(
            "class A", "{",
            "    private const int RequestTimeoutMs = 30000;",
            "    private readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(250);",
            "    private readonly TimeSpan BackoffSeconds = TimeSpan.FromSeconds(30);",
            "}");
        var result = await new TimeUnitConfusionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现时间单位陷阱", result);
    }

    [Fact]
    public async Task Time_ThresholdsAreSanelySized()
    {
        Assert.InRange(TimeUnitConfusionReportTool.SuspiciousMillisecondValues.Length, 3, 20);
        Assert.InRange(TimeUnitConfusionReportTool.SuspiciousSecondValues.Length, 3, 20);
    }

    [Fact]
    public async Task Time_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new TimeUnitConfusionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new TimeUnitConfusionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TimeUnitConfusionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
