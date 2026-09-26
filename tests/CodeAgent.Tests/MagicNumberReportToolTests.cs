using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class MagicNumberReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-magic-" + Guid.NewGuid().ToString("N"));

    public MagicNumberReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Magic_FindsRepeatedLiteral()
    {
        WriteSource(
            "class A",
            "{",
            "    int A1 = 4242;",
            "    int B1 = 4242;",
            "    int C1 = 4242;",
            "}");
        var result = await new MagicNumberReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("魔法数字报告: 1 个 .cs 文件", result);
        Assert.Contains("重复出现的字面量", result);
        Assert.Contains("4242", result);
    }

    [Fact]
    public async Task Magic_SingleOccurrenceIsNotRepeated()
    {
        WriteSource("class A", "{", "    int A1 = 4242;", "}");
        var result = await new MagicNumberReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("重复出现的字面量", result);
    }

    [Fact]
    public async Task Magic_FindsLiteralInComparison()
    {
        WriteSource("class A", "{", "    bool M(int n) => n > 4242;", "}");
        var result = await new MagicNumberReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("比较/算术里的裸字面量", result);
        Assert.Contains("应提为命名常量", result);
    }

    [Fact]
    public async Task Magic_FindsPortLikeValue()
    {
        WriteSource("class A", "{", "    int Port = 8080;", "}");
        var result = await new MagicNumberReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("像端口号", result);
    }

    [Fact]
    public async Task Magic_CommonValuesAreNotReported()
    {
        // 0/1/1024 之类是有意选值，报出来只是噪声
        WriteSource("class A", "{", "    int A1 = 0;", "    int B1 = 1;", "    int C1 = 1024;", "    int D1 = 0;", "    int E1 = 1;", "    int F1 = 1024;", "}");
        var result = await new MagicNumberReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现魔法数字问题", result);
    }

    [Fact]
    public async Task Magic_ThresholdIsSane()
    {
        Assert.InRange(MagicNumberReportTool.MinOccurrences, 2, 10);
    }

    [Fact]
    public async Task Magic_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new MagicNumberReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new MagicNumberReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MagicNumberReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
