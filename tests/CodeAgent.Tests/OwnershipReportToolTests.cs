using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class OwnershipReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-own-" + Guid.NewGuid().ToString("N"));

    public OwnershipReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Own_FindsDisposableWithoutDispose()
    {
        WriteSource(
            "class A : IDisposable", "{", "    public void Work()", "    {", "    }", "}");
        var result = await new OwnershipReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("资源所有权报告: 1 个 .cs 文件", result);
        Assert.Contains("IDisposable 却没 Dispose", result);
    }

    [Fact]
    public async Task Own_ImplementedDisposeIsClean()
    {
        WriteSource(
            "class A : IDisposable", "{",
            "    public void Dispose()", "    {", "        Clean();", "    }",
            "    void Clean() { }", "}");
        var result = await new OwnershipReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("IDisposable 却没 Dispose", result);
    }

    [Fact]
    public async Task Own_FindsUnreleasedParameter()
    {
        WriteSource(
            "class A", "{", "    void M(FileStream fs)", "    {", "        fs.ReadByte();", "    }", "}");
        var result = await new OwnershipReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("收到可释放参数但没说清所有权", result);
        Assert.Contains("FileStream", result);
    }

    [Fact]
    public async Task Own_UsingParameterIsClean()
    {
        WriteSource(
            "class A", "{", "    void M(FileStream fs)", "    {", "        fs.ReadByte();", "        fs.Dispose();", "    }", "}");
        var result = await new OwnershipReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("收到可释放参数但没说清所有权", result);
    }

    [Fact]
    public async Task Own_AmbiguousInjectionIsFlagged()
    {
        WriteSource(
            "class A", "{", "    private readonly HttpClient _client = new();", "}");
        var result = await new OwnershipReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("注入字段释放责任不明", result);
    }

    [Fact]
    public void TheOwnedListExcludesMemoryStream()
    {
        // MemoryStream 不占系统句柄，报出来只会淹没真问题
        Assert.DoesNotContain("MemoryStream", OwnershipReportTool.Owned);
        Assert.Contains("HttpClient", OwnershipReportTool.Owned);
    }

    [Fact]
    public async Task Own_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new OwnershipReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new OwnershipReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OwnershipReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
