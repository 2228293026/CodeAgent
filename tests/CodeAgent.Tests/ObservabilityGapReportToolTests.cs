using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ObservabilityGapReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-obs-" + Guid.NewGuid().ToString("N"));

    public ObservabilityGapReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Obs_FindsSilentCatch()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        try { N(); }",
            "        catch (IOException e)", "        {", "            Cleanup();", "        }", "    }",
            "    void N() { }", "    void Cleanup() { }", "}");
        var result = await new ObservabilityGapReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("可观测性缺口报告: 1 个 .cs 文件", result);
        Assert.Contains("catch 无痕迹", result);
        Assert.Contains("没有任何痕迹", result);
    }

    [Fact]
    public async Task Obs_FindsUnloggedWrite()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        File.WriteAllText(\"a\", \"x\");", "    }", "}");
        var result = await new ObservabilityGapReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("关键写操作无日志", result);
    }

    [Fact]
    public async Task Obs_LoggedCatchIsClean()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        try { N(); }",
            "        catch (IOException e)", "        {", "            Log.Error(e);", "        }", "    }",
            "    void N() { }", "}");
        var result = await new ObservabilityGapReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("catch 无痕迹", result);
    }

    [Fact]
    public async Task Obs_LogWithoutCorrelationId()
    {
        var body = new System.Collections.Generic.List<string> { "class A", "{" };
        for (var i = 0; i < 6; i++)
            body.Add("    void L" + i + "() { Log.Information(\"x\"); }");
        body.Add("}");
        WriteSource(body.ToArray());
        var result = await new ObservabilityGapReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("日志无关联标识", result);
        Assert.Contains("时间戳猜", result);
    }

    [Fact]
    public async Task Obs_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ObservabilityGapReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ObservabilityGapReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ObservabilityGapReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
