using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class StringInterpolationReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-str-" + Guid.NewGuid().ToString("N"));

    public StringInterpolationReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Str_FindsConcatInLoop()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        for (int i = 0; i < 10; i++)", "        {",
            "            var s = \"a\" + i + \"b\";", "        }", "    }", "}");
        var result = await new StringInterpolationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("字符串拼接报告: 1 个 .cs 文件", result);
        Assert.Contains("循环里 + 拼接", result);
        Assert.Contains("O(n²)", result);
    }

    [Fact]
    public async Task Str_FindsManyConcats()
    {
        WriteSource("class A", "{", "    string M(string a, string b, string c)", "    {", "        return \"x\" + a + \"y\" + b + \"z\" + c;", "    }", "}");
        var result = await new StringInterpolationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("多段拼接未用插值", result);
        Assert.Contains("插值", result);
    }

    [Fact]
    public async Task Str_InterpolationIsClean()
    {
        WriteSource("class A", "{", "    string M(string a, string b)", "    {", "        return $\"x{a}y{b}z\";", "    }", "}");
        var result = await new StringInterpolationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现字符串拼接问题", result);
    }

    [Fact]
    public async Task Str_FindsLogConcatInLoop()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        for (int i = 0; i < 10; i++)", "        {",
            "            Log(\"tick \" + i);", "        }", "    }", "    void Log(string s) { }", "}");
        var result = await new StringInterpolationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("循环里拼日志", result);
    }

    [Fact]
    public void TheLogCallListExcludesNonLoggingMethods()
    {
        Assert.Contains("LogError", StringInterpolationReportTool.LogCalls);
        Assert.Contains("Console.WriteLine", StringInterpolationReportTool.LogCalls);
        // 这些不是日志，不该混进表里
        Assert.DoesNotContain("Append", StringInterpolationReportTool.LogCalls);
    }

    [Fact]
    public async Task Str_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new StringInterpolationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new StringInterpolationReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StringInterpolationReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
