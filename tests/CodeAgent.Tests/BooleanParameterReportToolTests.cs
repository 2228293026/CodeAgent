using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class BooleanParameterReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-boolparam-" + Guid.NewGuid().ToString("N"));

    public BooleanParameterReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Bool_FindsOptionalAfterRequired()
    {
        WriteSource("class A", "{", "    void M(int a, int b = 3)", "    {", "    }", "}");
        var result = await new BooleanParameterReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("布尔参数报告: 1 个 .cs 文件", result);
        Assert.Contains("可选参数排在必选参数之后", result);
        Assert.Contains("b", result);
    }

    [Fact]
    public async Task Bool_FindsSemanticallyVagueFlag()
    {
        WriteSource("class A", "{", "    void M(int a, bool flag)", "    {", "    }", "}");
        var result = await new BooleanParameterReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("语义不明的布尔参数", result);
        Assert.Contains("flag", result);
    }

    [Fact]
    public async Task Bool_PrefixedFlagsAreFine()
    {
        WriteSource(
            "class A", "{",
            "    void M(bool isValid, bool hasMore, bool enableCache, bool forceOverwrite)", "    {", "    }", "}");
        var result = await new BooleanParameterReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现布尔参数陷阱", result);
    }

    [Fact]
    public async Task Bool_RequiredBeforeOptionalIsFine()
    {
        WriteSource("class A", "{", "    void M(int a = 1, int b = 2)", "    {", "    }", "}");
        var result = await new BooleanParameterReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现布尔参数陷阱", result);
    }

    [Fact]
    public async Task Bool_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new BooleanParameterReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new BooleanParameterReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BooleanParameterReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
