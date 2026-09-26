using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class NullableAnnotationReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-nullable-" + Guid.NewGuid().ToString("N"));

    public NullableAnnotationReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Nullable_FindsDisableDirective()
    {
        WriteSource("#nullable disable", "class A", "{", "}");
        var result = await new NullableAnnotationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("可空性标注报告: 1 个 .cs 文件", result);
        Assert.Contains("#nullable disable", result);
        Assert.Contains("静默", result);
    }

    [Fact]
    public async Task Nullable_FindsBareReferenceField()
    {
        WriteSource("#nullable enable", "class A", "{", "    public string Name = \"\";", "}");
        var result = await new NullableAnnotationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("裸引用类型", result);
        Assert.Contains("Name", result);
    }

    [Fact]
    public async Task Nullable_AnnotatedFieldIsFine()
    {
        WriteSource("#nullable enable", "class A", "{", "    public string? Name;", "}");
        var result = await new NullableAnnotationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("裸引用类型", result);
    }

    [Fact]
    public async Task Nullable_FindsNonNullableCollectionElement()
    {
        WriteSource("#nullable enable", "class A", "{", "    public List<string> Names = new();", "}");
        var result = await new NullableAnnotationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("集合元素未标 ?", result);
        Assert.Contains("List<string?>", result);
    }

    [Fact]
    public async Task Nullable_FileWithoutDirectiveIsNotJudged()
    {
        // 没写 #nullable enable 的文件由项目级设置决定，工具不该臆断
        WriteSource("class A", "{", "    public string Name = \"\";", "}");
        var result = await new NullableAnnotationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("裸引用类型", result);
    }

    [Fact]
    public async Task Nullable_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new NullableAnnotationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现可空性标注问题", result);
    }

    [Fact]
    public async Task Nullable_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new NullableAnnotationReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new NullableAnnotationReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NullableAnnotationReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
