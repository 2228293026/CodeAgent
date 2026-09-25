using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class InputValidationReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-inputval-" + Guid.NewGuid().ToString("N"));

    public InputValidationReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task InputValidationReport_FindsUnguardedEnvVar()
    {
        WriteSource(
            "class A",
            "{",
            "    string M() => Environment.GetEnvironmentVariable(\"API_KEY\");",
            "}");
        var result = await new InputValidationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("输入校验报告: 1 个 .cs 文件", result);
        Assert.Contains("环境变量未判空: 1", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task InputValidationReport_AcceptsGuardedEnvVar()
    {
        WriteSource(
            "class A",
            "{",
            "    string M() => Environment.GetEnvironmentVariable(\"API_KEY\") ?? \"default\";",
            "}");
        var result = await new InputValidationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("环境变量未判空", result);
    }

    [Fact]
    public async Task InputValidationReport_FlagsParseInsteadOfTryParse()
    {
        WriteSource(
            "class A",
            "{",
            "    int M(string s) => int.Parse(s);",
            "    int N(string s) => int.TryParse(s, out var v) ? v : 0;",
            "}");
        var result = await new InputValidationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Parse 而非 TryParse: 1", result);
        Assert.Contains("不可信输入会抛异常", result);
    }

    [Fact]
    public async Task InputValidationReport_FindsArgWithoutLengthCheck()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(string[] args)",
            "    {",
            "        Use(args[0]);",
            "    }",
            "}");
        var result = await new InputValidationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("命令行参数未查长度: 1", result);
    }

    [Fact]
    public async Task InputValidationReport_CleanCodeHasNoGaps()
    {
        WriteSource(
            "class A",
            "{",
            "    int M(string s) => int.TryParse(s, out var v) ? v : 0;",
            "}");
        var result = await new InputValidationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现输入校验缺口", result);
    }

    [Fact]
    public async Task InputValidationReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new InputValidationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new InputValidationReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InputValidationReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
