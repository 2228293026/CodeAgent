using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class GrepToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-grep-" + Guid.NewGuid().ToString("N"));

    public GrepToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private static AgentContext MakeContext(string dir) => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(dir),
    };

    [Fact]
    public async Task Grep_Include_FiltersFilesByGlob()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "TODO fix this\n");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "TODO fix this\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "TODO", ["include"] = "*.cs" }, ctx, CancellationToken.None);
        Assert.Contains("a.cs:1", output);
        Assert.DoesNotContain("a.txt", output); // include 排除了 .txt
    }

    [Fact]
    public async Task Grep_Exclude_SkipsMatchingFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "TODO fix this\n");
        File.WriteAllText(Path.Combine(_dir, "gen.cs"), "TODO fix this\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "TODO", ["exclude"] = "gen.cs" }, ctx, CancellationToken.None);
        Assert.Contains("a.cs:1", output);
        Assert.DoesNotContain("gen.cs", output); // exclude 跳过了 gen.cs
    }

    [Fact]
    public async Task Grep_IncludeAsArray_Works()
    {
        File.WriteAllText(Path.Combine(_dir, "x.cs"), "FIXME\n");
        File.WriteAllText(Path.Combine(_dir, "y.md"), "FIXME\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "FIXME", ["include"] = new JsonArray("*.cs") }, ctx, CancellationToken.None);
        Assert.Contains("x.cs:1", output);
        Assert.DoesNotContain("y.md", output);
    }

    [Fact]
    public async Task Grep_FilesOnly_ReturnsPathsWithoutLines()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "TODO first\n");
        File.WriteAllText(Path.Combine(_dir, "b.cs"), "TODO second\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "TODO", ["files_only"] = true }, ctx, CancellationToken.None);
        Assert.Contains("a.cs", output);
        Assert.Contains("b.cs", output);
        Assert.DoesNotContain("TODO first", output); // 不返回行内容
        Assert.DoesNotContain(":1:", output);        // 不返回行号
        Assert.Contains("个文件", output);
    }

    [Fact]
    public async Task Grep_FilesOnly_CountsEachFileOnce()
    {
        // 同一文件多行匹配时 files_only 只计一次
        File.WriteAllText(Path.Combine(_dir, "multi.cs"), "TODO a\nTODO b\nTODO c\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "TODO", ["files_only"] = true }, ctx, CancellationToken.None);
        Assert.Contains("1 个文件", output); // 3 行匹配但只算 1 个文件
        Assert.Equal(1, output.Split('\n').Count(l => l.Contains("multi.cs")));
    }
}
