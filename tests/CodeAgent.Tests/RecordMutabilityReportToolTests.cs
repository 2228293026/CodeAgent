using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class RecordMutabilityReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-recordmut-" + Guid.NewGuid().ToString("N"));

    public RecordMutabilityReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Record_FindsMutableCollectionField()
    {
        WriteSource(
            "public record Point",
            "{",
            "    public List<int> Values { get; init; } = new();",
            "}");
        var result = await new RecordMutabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("record 可变性报告: 1 个 .cs 文件", result);
        Assert.Contains("持有可变集合字段", result);
        Assert.Contains("值语义失效", result);
    }

    [Fact]
    public async Task Record_ImmutableFieldsAreFine()
    {
        WriteSource(
            "public record Point",
            "{",
            "    public int X { get; init; }",
            "    public string Name { get; init; } = \"\";",
            "}");
        var result = await new RecordMutabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("持有可变集合字段", result);
    }

    [Fact]
    public async Task Record_FindsMutatingMember()
    {
        WriteSource(
            "public record Bag",
            "{",
            "    public void Add(int n) { }",
            "}");
        var result = await new RecordMutabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("改状态的成员", result);
    }

    [Fact]
    public async Task Record_FindsRecordStruct()
    {
        WriteSource(
            "public record struct Vec",
            "{",
            "    public int X { get; set; }",
            "}");
        var result = await new RecordMutabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("record struct", result);
        Assert.Contains("可变值类型", result);
    }

    [Fact]
    public async Task Record_PlainClassIsNotFlagged()
    {
        WriteSource("public class Bag", "{", "    public List<int> Values { get; } = new();", "}");
        var result = await new RecordMutabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 record 可变性问题", result);
    }

    [Fact]
    public async Task Record_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new RecordMutabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new RecordMutabilityReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RecordMutabilityReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
