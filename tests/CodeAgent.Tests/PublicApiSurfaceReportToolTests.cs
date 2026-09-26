using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class PublicApiSurfaceReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-api-" + Guid.NewGuid().ToString("N"));

    public PublicApiSurfaceReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Api_FindsMutableDefault()
    {
        WriteSource("class A", "{", "    public List<int> Items { get; set; } = new List<int>();", "}");
        var result = await new PublicApiSurfaceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("公开 API 报告: 1 个 .cs 文件", result);
        Assert.Contains("可变默认值", result);
        Assert.Contains("共享同一实例", result);
    }

    [Fact]
    public async Task Api_FindsPublicField()
    {
        WriteSource("class A", "{", "    public int Count;", "}");
        var result = await new PublicApiSurfaceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("公开字段", result);
        Assert.Contains("Count", result);
    }

    [Fact]
    public async Task Api_FindsPositionalBoolParameter()
    {
        WriteSource("class A", "{", "    public void Run(bool force, int n)", "    {", "    }", "}");
        var result = await new PublicApiSurfaceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("只能靠位置传的 bool 参数", result);
        Assert.Contains("读不出含义", result);
    }

    [Fact]
    public async Task Api_PropertiesAndPrivateMembersAreFine()
    {
        WriteSource("class A", "{", "    public int Count { get; set; }", "    private List<int> Items = new List<int>();", "}");
        var result = await new PublicApiSurfaceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现公开 API 稳定性问题", result);
    }

    [Fact]
    public async Task Api_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new PublicApiSurfaceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new PublicApiSurfaceReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PublicApiSurfaceReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
