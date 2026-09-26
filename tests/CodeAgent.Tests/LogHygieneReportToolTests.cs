using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LogHygieneReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-loghyg-" + Guid.NewGuid().ToString("N"));

    public LogHygieneReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task LogHygiene_FindsSecretInLog()
    {
        WriteSource("class A", "{", "    void M(string apiKey) => System.Console.WriteLine(apiKey);", "}");
        var result = await new LogHygieneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("日志卫生报告: 1 个 .cs 文件", result);
        Assert.Contains("日志含敏感字段", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task LogHygiene_FindsFullPayloadInException()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        throw new InvalidOperationException($\"request body: {body}\");",
            "    }",
            "}");
        var result = await new LogHygieneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("异常夹带全文", result);
    }

    [Fact]
    public async Task LogHygiene_ExceptionWithOnlyLengthIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        throw new InvalidOperationException($\"request body Length: {body.Length}\");",
            "    }",
            "}");
        var result = await new LogHygieneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("异常夹带全文", result);
    }

    [Fact]
    public async Task LogHygiene_FindsDebugResidue()
    {
        WriteSource("class A", "{", "    void M() => Console.WriteLine(\"tmp\");", "}");
        var result = await new LogHygieneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("调试残留", result);
        Assert.Contains("统一日志", result);
    }

    [Fact]
    public async Task LogHygiene_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new LogHygieneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现日志卫生问题", result);
    }

    [Fact]
    public async Task LogHygiene_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new LogHygieneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new LogHygieneReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LogHygieneReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
