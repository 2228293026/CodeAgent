using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DependencyInversionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-di-" + Guid.NewGuid().ToString("N"));

    public DependencyInversionReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Di_FindsConcreteTypedField()
    {
        WriteSource("class A", "{", "    private SqlConnection _conn;", "}");
        var result = await new DependencyInversionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("依赖倒置报告: 1 个 .cs 文件", result);
        Assert.Contains("字段声明成具体类型", result);
        Assert.Contains("_conn", result);
    }

    [Fact]
    public async Task Di_InterfaceTypedFieldIsFine()
    {
        WriteSource("interface IStore", "{ }", "class A", "{", "    private IStore _store;", "}");
        var result = await new DependencyInversionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("字段声明成具体类型", result);
    }

    [Fact]
    public async Task Di_FindsDirectInfrastructureConstruction()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        var c = new HttpClient();", "    }", "}");
        var result = await new DependencyInversionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("直接 new 基础设施", result);
        Assert.Contains("HttpClient", result);
    }

    [Fact]
    public async Task Di_TestDoublesAreNotFlagged()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        var c = new HttpClientFake();", "    }", "}");
        var result = await new DependencyInversionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现依赖倒置缺口", result);
    }

    [Fact]
    public async Task Di_FindsConcreteConstructorParameter()
    {
        WriteSource("class A", "{", "    public A(SqlConnection conn)", "    {", "    }", "}");
        var result = await new DependencyInversionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("构造参数是具体类型", result);
    }

    [Fact]
    public void TheInfraMarkerListIsCurated()
    {
        Assert.InRange(DependencyInversionReportTool.InfraMarkers.Length, 5, 40);
        Assert.Contains("HttpClient", DependencyInversionReportTool.InfraMarkers);
        // 接口/抽象不能混进来：那不是"基础设施"
        Assert.DoesNotContain("List", DependencyInversionReportTool.InfraMarkers);
    }

    [Fact]
    public async Task Di_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DependencyInversionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DependencyInversionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DependencyInversionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
