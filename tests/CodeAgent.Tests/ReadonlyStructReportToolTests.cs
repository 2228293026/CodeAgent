using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ReadonlyStructReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-ro-" + Guid.NewGuid().ToString("N"));

    public ReadonlyStructReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Ro_FindsSetterInReadonlyStruct()
    {
        WriteSource(
            "public readonly struct S", "{", "    public int X { get; set; }", "}");
        var result = await new ReadonlyStructReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("readonly struct 报告: 1 个 .cs 文件", result);
        Assert.Contains("readonly struct 里有 setter", result);
        Assert.Contains("名不副实", result);
    }

    [Fact]
    public async Task Ro_GetOnlyReadonlyStructIsClean()
    {
        WriteSource("public readonly struct S", "{", "    public int X { get; }", "    public S(int x) { X = x; }", "}");
        var result = await new ReadonlyStructReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 readonly struct 问题", result);
    }

    [Fact]
    public async Task Ro_FindsMutableStructAsCollectionKey()
    {
        WriteSource(
            "using System.Collections.Generic;", "public struct Point", "{", "    public int X;",
            "}", "class A", "{", "    HashSet<Point> set = new();",
            "    void M(Point p)", "    {", "        set.Add(p);", "        p.X = 5;", "    }", "}");
        var result = await new ReadonlyStructReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("可变 struct 当集合键", result);
        Assert.Contains("set.Contains", result);
    }

    [Fact]
    public async Task Ro_PlainClassWithSetterIsClean()
    {
        // class 有 setter 完全是正常写法，不该被当成 readonly struct 问题
        WriteSource("public class C", "{", "    public int X { get; set; }", "}");
        var result = await new ReadonlyStructReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 readonly struct 问题", result);
    }

    [Fact]
    public async Task Ro_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ReadonlyStructReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ReadonlyStructReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReadonlyStructReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
