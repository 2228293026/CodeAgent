using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class SwitchExhaustivenessReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-switch-" + Guid.NewGuid().ToString("N"));

    public SwitchExhaustivenessReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Switch_FindsMissingDefault()
    {
        WriteSource("class A", "{", "    int M(int x)", "    {", "        switch (x)", "        {", "            case 1: return 1;", "        }", "        return 0;", "    }", "}");
        var result = await new SwitchExhaustivenessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("switch 完备性报告: 1 个 .cs 文件", result);
        Assert.Contains("缺 default", result);
    }

    [Fact]
    public async Task Switch_FindsMissingEnumMember()
    {
        WriteSource(
            "enum Level", "{", "    Low,", "    High,", "}",
            "class A", "{", "    int M(Level l)", "    {", "        switch (l)", "        {", "            case Level.Low: return 1;", "            default: return 0;", "        }", "    }", "}");
        var result = await new SwitchExhaustivenessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("漏掉的 enum 成员", result);
        Assert.Contains("Level.High", result);
    }

    [Fact]
    public async Task Switch_FindsDuplicateCase()
    {
        WriteSource(
            "class A", "{", "    int M(int x)", "    {", "        switch (x)", "        {", "            case 1: return 1;", "            case 1: return 2;", "            default: return 0;", "        }", "    }", "}");
        var result = await new SwitchExhaustivenessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重复的 case", result);
        Assert.Contains("永远不可达", result);
    }

    [Fact]
    public async Task Switch_CompleteSwitchIsClean()
    {
        WriteSource(
            "enum Level", "{", "    Low,", "    High,", "}",
            "class A", "{", "    int M(Level l)", "    {", "        switch (l)", "        {", "            case Level.Low: return 1;", "            case Level.High: return 2;", "            default: return 0;", "        }", "    }", "}");
        var result = await new SwitchExhaustivenessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 switch 完备性问题", result);
    }

    [Fact]
    public async Task Switch_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new SwitchExhaustivenessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new SwitchExhaustivenessReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SwitchExhaustivenessReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
