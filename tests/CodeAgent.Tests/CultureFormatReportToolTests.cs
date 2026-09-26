using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CultureFormatReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-culture-" + Guid.NewGuid().ToString("N"));

    public CultureFormatReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Culture_FindsToStringWithoutCulture()
    {
        WriteSource("class A", "{", "    string M(int n) => n.ToString();", "}");
        var result = await new CultureFormatReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("区域设置报告: 1 个 .cs 文件", result);
        Assert.Contains("ToString 未指定文化", result);
    }

    [Fact]
    public async Task Culture_FindsParseWithoutCulture()
    {
        WriteSource("class A", "{", "    int M(string s) => int.Parse(s);", "}");
        var result = await new CultureFormatReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Parse 未指定文化", result);
        Assert.Contains("随机器语言变化", result);
    }

    [Fact]
    public async Task Culture_FindsCasingWithoutInvariant()
    {
        WriteSource("class A", "{", "    string M(string s) => s.ToUpper();", "}");
        var result = await new CultureFormatReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("ToUpper/ToLower 未用不变文化", result);
        Assert.Contains("土耳其语", result);
    }

    [Fact]
    public async Task Culture_ExplicitCultureIsFine()
    {
        WriteSource(
            "using System.Globalization;",
            "class A", "{",
            "    string M(int n) => n.ToString(CultureInfo.InvariantCulture);",
            "    int N(string s) => int.Parse(s, CultureInfo.InvariantCulture);",
            "    string P(string s) => s.ToUpperInvariant();",
            "}");
        var result = await new CultureFormatReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现区域设置陷阱", result);
    }

    [Fact]
    public async Task Culture_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new CultureFormatReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new CultureFormatReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CultureFormatReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
