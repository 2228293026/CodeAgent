using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TimeZoneReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-tz-" + Guid.NewGuid().ToString("N"));

    public TimeZoneReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Tz_FindsNowAndUtcNowMix()
    {
        WriteSource(
            "class A", "{", "    int M()", "    {",
            "        return (int)(DateTime.Now - DateTime.UtcNow).TotalSeconds;", "    }", "}");
        var result = await new TimeZoneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("时区报告: 1 个 .cs 文件", result);
        Assert.Contains("Now/UtcNow 混用", result);
    }

    [Fact]
    public async Task Tz_FindsKindLossBeforeConversion()
    {
        WriteSource(
            "class A", "{", "    DateTime M(string s)", "    {",
            "        return DateTime.Parse(s).ToLocalTime();", "    }", "}");
        var result = await new TimeZoneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Kind 丢失后换算", result);
    }

    [Fact]
    public async Task Tz_FindsDateTimeAsInstant()
    {
        WriteSource(
            "class A", "{", "    DateTime createdAt;", "}", "class B", "{",
            "    Dictionary<string, DateTime> Cache;", "}");
        var result = await new TimeZoneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("用 DateTime 存时刻", result);
    }

    [Fact]
    public async Task Tz_DateTimeOffsetIsFine()
    {
        WriteSource(
            "class A", "{", "    DateTimeOffset createdAt;", "    void M()", "    {",
            "        var d = DateTimeOffset.Now.ToUniversalTime();", "    }", "}");
        var result = await new TimeZoneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("用 DateTime 存时刻", result);
        Assert.DoesNotContain("Now/UtcNow 混用", result);
    }

    [Fact]
    public async Task Tz_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new TimeZoneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new TimeZoneReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TimeZoneReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
