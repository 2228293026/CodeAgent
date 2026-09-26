using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ObsoleteUsageReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-obsolete-" + Guid.NewGuid().ToString("N"));

    public ObsoleteUsageReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Obsolete_FindsStillUsedMember()
    {
        WriteSource(
            "class A",
            "{",
            "    [System.Obsolete(\"改用 NewThing\")]",
            "    public void OldThing() { }",
            "    public void Caller() { OldThing(); }",
            "}");
        var result = await new ObsoleteUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Obsolete 使用报告: 1 个 .cs 文件", result);
        Assert.Contains("标注了却仍在用", result);
        Assert.Contains("OldThing", result);
    }

    [Fact]
    public async Task Obsolete_UnusedMemberIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    [System.Obsolete(\"改用 NewThing\")]",
            "    public void OldThing() { }",
            "    public void Caller() { }",
            "}");
        var result = await new ObsoleteUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("标注了却仍在用", result);
    }

    [Fact]
    public async Task Obsolete_FindsErrorTrueStillUsed()
    {
        WriteSource(
            "class A",
            "{",
            "    [System.Obsolete(\"改用 NewThing\", error: true)]",
            "    public void OldThing() { }",
            "    public void Caller() { OldThing(); }",
            "}");
        var result = await new ObsoleteUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("error:true 仍在用", result);
        Assert.Contains("本应编译不过", result);
    }

    [Fact]
    public async Task Obsolete_FindsMissingReplacement()
    {
        WriteSource(
            "class A",
            "{",
            "    [System.Obsolete(\"过时了\")]",
            "    public void OldThing() { }",
            "}");
        var result = await new ObsoleteUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未写替代方案", result);
        Assert.Contains("无从判断", result);
    }

    [Fact]
    public async Task Obsolete_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new ObsoleteUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 Obsolete 使用问题", result);
    }

    [Fact]
    public async Task Obsolete_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ObsoleteUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ObsoleteUsageReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ObsoleteUsageReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
