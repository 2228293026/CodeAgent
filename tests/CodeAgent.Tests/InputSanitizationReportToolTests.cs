using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class InputSanitizationReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-inputsan-" + Guid.NewGuid().ToString("N"));

    public InputSanitizationReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task InputSanitization_FindsUnquotedShellConcatenation()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(string name)",
            "    {",
            "        System.Diagnostics.Process.Start(\"/bin/sh\", \"-c ls \" + name);",
            "    }",
            "}");
        var result = await new InputSanitizationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("输入处理报告: 1 个 .cs 文件", result);
        Assert.Contains("shell 拼接未加引号: 1", result);
        Assert.Contains("A.cs:5", result);
    }

    [Fact]
    public async Task InputSanitization_QuotedShellIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(string name)",
            "    {",
            "        System.Diagnostics.Process.Start(\"/bin/sh\", $\"-c 'ls {name}'\");",
            "    }",
            "}");
        var result = await new InputSanitizationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("shell 拼接未加引号", result);
    }

    [Fact]
    public async Task InputSanitization_FindsUnboundedInput()
    {
        WriteSource("class A", "{", "    string M() => Console.ReadLine();", "}");
        var result = await new InputSanitizationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("外部输入无上限", result);
        Assert.Contains("未校验长度/范围", result);
    }

    [Fact]
    public async Task InputSanitization_BoundedInputIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    string M()",
            "    {",
            "        var s = Console.ReadLine();",
            "        return s.Length > 100 ? null : s;",
            "    }",
            "}");
        var result = await new InputSanitizationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("外部输入无上限", result);
    }

    [Fact]
    public async Task InputSanitization_FindsUnvalidatedToolArg()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        var path = ToolArgs.GetString(args, \"path\");",
            "        Use(path);",
            "    }",
            "}");
        var result = await new InputSanitizationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("工具参数未校验", result);
        Assert.Contains("path", result);
    }

    [Fact]
    public async Task InputSanitization_ValidatedToolArgIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        var path = ToolArgs.GetString(args, \"path\");",
            "        if (string.IsNullOrWhiteSpace(path)) throw new ToolException(\"缺参数\");",
            "        Use(path);",
            "    }",
            "}");
        var result = await new InputSanitizationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("工具参数未校验", result);
    }

    [Fact]
    public async Task InputSanitization_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new InputSanitizationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现输入处理问题", result);
    }

    [Fact]
    public async Task InputSanitization_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new InputSanitizationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new InputSanitizationReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InputSanitizationReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
