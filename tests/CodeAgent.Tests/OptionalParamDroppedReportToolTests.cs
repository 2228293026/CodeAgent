using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class OptionalParamDroppedReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-optpar-" + Guid.NewGuid().ToString("N"));

    public OptionalParamDroppedReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task OptionalParam_FindsLostDefault()
    {
        WriteSource(
            "public interface IRunner",
            "{",
            "    void Run(string name, int retries = 3);",
            "}",
            "public class Runner : IRunner",
            "{",
            "    public void Run(string name, int retries) { }",
            "}");
        var result = await new OptionalParamDroppedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("接口签名漂移报告: 1 个 .cs 文件", result);
        Assert.Contains("实现丢了默认值", result);
        Assert.Contains("Run", result);
    }

    [Fact]
    public async Task OptionalParam_KeptDefaultIsFine()
    {
        WriteSource(
            "public interface IRunner",
            "{",
            "    void Run(string name, int retries = 3);",
            "}",
            "public class Runner : IRunner",
            "{",
            "    public void Run(string name, int retries = 3) { }",
            "}");
        var result = await new OptionalParamDroppedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("实现丢了默认值", result);
    }

    [Fact]
    public async Task OptionalParam_FindsLostRefModifier()
    {
        WriteSource(
            "public interface IStore",
            "{",
            "    void Put(ref int value);",
            "}",
            "public class Store : IStore",
            "{",
            "    public void Put(int value) { }",
            "}");
        var result = await new OptionalParamDroppedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("实现去掉了 ref/out/in", result);
    }

    [Fact]
    public async Task OptionalParam_NoInterfaceIsClean()
    {
        WriteSource("class A", "{", "    void Run(string name, int retries) { }", "}");
        var result = await new OptionalParamDroppedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现接口签名漂移", result);
    }

    [Fact]
    public async Task OptionalParam_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new OptionalParamDroppedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new OptionalParamDroppedReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OptionalParamDroppedReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
