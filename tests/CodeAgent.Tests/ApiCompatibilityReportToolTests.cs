using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ApiCompatibilityReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-apicompat-" + Guid.NewGuid().ToString("N"));

    public ApiCompatibilityReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ApiCompatibilityReport_FindsStillUsedDeprecatedMember()
    {
        WriteSource(
            "class A",
            "{",
            "    [Obsolete(\"改用 NewWay\")]",
            "    public void OldWay() { }",
            "",
            "    public void Caller() => OldWay();",
            "}");
        var result = await new ApiCompatibilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("API 兼容性报告: 1 个 .cs 文件", result);
        Assert.Contains("1 个已废弃成员", result);
        Assert.Contains("仍在使用已废弃成员", result);
        Assert.Contains("OldWay()", result);
    }

    [Fact]
    public async Task ApiCompatibilityReport_ObsoleteWithDocIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    /// <summary>请改用 NewWay。</summary>",
            "    [Obsolete(\"改用 NewWay\")]",
            "    public void OldWay() { }",
            "}");
        var result = await new ApiCompatibilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("Obsolete 缺说明", result);
    }

    [Fact]
    public async Task ApiCompatibilityReport_FindsUndocumentedObsolete()
    {
        WriteSource("class A", "{", "    [Obsolete(\"改用 NewWay\")]", "    public void OldWay() { }", "}");
        var result = await new ApiCompatibilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Obsolete 缺说明: 1", result);
        Assert.Contains("没有说明替代方案", result);
    }

    [Fact]
    public async Task ApiCompatibilityReport_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    /// <summary>正常方法。</summary>", "    public int Good() => 3;", "}");
        var result = await new ApiCompatibilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 API 兼容性问题", result);
    }

    [Fact]
    public async Task ApiCompatibilityReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ApiCompatibilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ApiCompatibilityReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ApiCompatibilityReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
