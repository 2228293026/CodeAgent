using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DataContractReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-datacontract-" + Guid.NewGuid().ToString("N"));

    public DataContractReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task DataContractReport_FindsMixedFieldAndProperty()
    {
        WriteSource(
            "class Payload",
            "{",
            "    [JsonPropertyName(\"a\")] public int FieldA;",
            "    [JsonPropertyName(\"b\")] public string PropB { get; set; }",
            "}");
        var result = await new DataContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("数据契约报告: 1 个 .cs 文件", result);
        Assert.Contains("字段与属性混用", result);
        Assert.Contains("Payload", result);
    }

    [Fact]
    public async Task DataContractReport_AllPropertiesIsFine()
    {
        WriteSource(
            "class Payload",
            "{",
            "    [JsonPropertyName(\"a\")] public int PropA { get; set; }",
            "    [JsonPropertyName(\"b\")] public string PropB { get; set; }",
            "}");
        var result = await new DataContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("字段与属性混用", result);
    }

    [Fact]
    public async Task DataContractReport_FindsObjectDictionaryKey()
    {
        WriteSource(
            "class A",
            "{",
            "    System.Collections.Generic.Dictionary<object, string> Map = new();",
            "}");
        var result = await new DataContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("字典键用 object", result);
        Assert.Contains("所有键变字符串", result);
    }

    [Fact]
    public async Task DataContractReport_TypedDictionaryKeyIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    System.Collections.Generic.Dictionary<int, string> Map = new();",
            "}");
        var result = await new DataContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("字典键用 object", result);
    }

    [Fact]
    public async Task DataContractReport_ConstAndReadonlyStaticAreFine()
    {
        WriteSource(
            "class A",
            "{",
            "    const int N = 3;",
            "    static readonly string S = \"x\";",
            "}");
        var result = await new DataContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("可变 static 状态", result);
    }

    [Fact]
    public async Task DataContractReport_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new DataContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现数据契约问题", result);
    }

    [Fact]
    public async Task DataContractReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DataContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DataContractReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DataContractReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
