using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class JsonSchemaReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-jsonschema-" + Guid.NewGuid().ToString("N"));

    public JsonSchemaReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    [Theory]
    [InlineData("http://json-schema.org/draft-07/schema#", "draft-07")]
    [InlineData("https://json-schema.org/draft/2020-12/schema", "2020-12")]
    [InlineData("http://json-schema.org/draft-04/schema#", "draft-04")]
    [InlineData("https://example.com/other.json", null)]
    [InlineData(null, null)]
    public void DraftOf_ExtractsDraftVersion(string? schema, string? expected) =>
        Assert.Equal(expected, JsonSchemaReportTool.DraftOf(schema));

    [Fact]
    public async Task JsonSchemaReport_ListsIdsTitlesAndDrafts()
    {
        File.WriteAllText(Path.Combine(_dir, "a.schema.json"),
            """{"$schema":"http://json-schema.org/draft-07/schema#","$id":"https://x.dev/a","title":"A","type":"object"}""");
        File.WriteAllText(Path.Combine(_dir, "b.schema.json"),
            """{"$schema":"https://json-schema.org/draft/2020-12/schema","title":"B","type":"array"}""");
        var result = await new JsonSchemaReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("JSON Schema 报告: 2 个 schema", result);
        Assert.Contains("draft-07: 1", result);
        Assert.Contains("2020-12: 1", result);
        Assert.Contains("缺少 $id: 1   缺少 title: 0", result);
        Assert.Contains("https://x.dev/a", result);
        Assert.Contains("(无 $id)", result);
    }

    [Fact]
    public async Task JsonSchemaReport_ReportsUnparsableFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "bad.schema.json"), "{ not json");
        var result = await new JsonSchemaReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("解析失败: 1 个", result);
    }

    [Fact]
    public async Task JsonSchemaReport_IncludeAllJsonToggle()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"title":"C"}""");
        var off = await new JsonSchemaReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 schema 文件", off);
        var on = await new JsonSchemaReportTool().ExecuteAsync(
            new JsonObject { ["include_all_json"] = true }, Context(), CancellationToken.None);
        Assert.Contains("1 个 schema", on);
    }

    [Fact]
    public async Task JsonSchemaReport_RejectsOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new JsonSchemaReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "a.schema.json"), "{}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new JsonSchemaReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
