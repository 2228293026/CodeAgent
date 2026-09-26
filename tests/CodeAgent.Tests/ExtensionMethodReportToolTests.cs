using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ExtensionMethodReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-extmethod-" + Guid.NewGuid().ToString("N"));

    public ExtensionMethodReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Extension_FindsNonStaticClass()
    {
        WriteSource(
            "public class Helpers",
            "{",
            "    public static int Twice(this int n) => n * 2;",
            "}");
        var result = await new ExtensionMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("扩展方法报告: 1 个 .cs 文件", result);
        Assert.Contains("非 static 类里的扩展方法", result);
        Assert.Contains("CS1106", result);
    }

    [Fact]
    public async Task Extension_StaticClassIsFine()
    {
        WriteSource(
            "public static class Helpers",
            "{",
            "    public static int Twice(this int n) => n * 2;",
            "}");
        var result = await new ExtensionMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("非 static 类里的扩展方法", result);
    }

    [Fact]
    public async Task Extension_FindsMissingThis()
    {
        WriteSource(
            "public static class Helpers",
            "{",
            "    public static int Twice(int n) => n * 2;",
            "}");
        var result = await new ExtensionMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("首参缺 this", result);
        Assert.Contains("Twice", result);
    }

    [Fact]
    public async Task Extension_FindsNestedClass()
    {
        WriteSource(
            "public class Outer",
            "{",
            "    public static class Helpers",
            "    {",
            "        public static int Twice(this int n) => n * 2;",
            "    }",
            "}");
        var result = await new ExtensionMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("定义在嵌套类", result);
    }

    [Fact]
    public async Task Extension_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new ExtensionMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现扩展方法问题", result);
    }

    [Fact]
    public async Task Extension_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ExtensionMethodReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ExtensionMethodReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExtensionMethodReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
