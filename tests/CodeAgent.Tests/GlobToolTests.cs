using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class GlobToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-glob-" + Guid.NewGuid().ToString("N"));

    public GlobToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Glob_SinglePattern_StillWorks()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "");
        var tool = new GlobTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "*.cs" }, ctx, CancellationToken.None);
        Assert.Contains("a.cs", output);
        Assert.DoesNotContain("b.txt", output);
    }

    [Fact]
    public async Task Glob_MultiplePatterns_MatchesAny()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "");
        File.WriteAllText(Path.Combine(_dir, "b.rs"), "");
        File.WriteAllText(Path.Combine(_dir, "c.md"), "");
        var tool = new GlobTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = new JsonArray("*.cs", "*.rs") }, ctx, CancellationToken.None);
        Assert.Contains("a.cs", output);
        Assert.Contains("b.rs", output);
        Assert.DoesNotContain("c.md", output); // 不匹配任一模式
    }

    [Fact]
    public async Task Glob_NoMatch_ReportsPatterns()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "");
        var tool = new GlobTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = new JsonArray("*.xyz", "*.zzz") }, ctx, CancellationToken.None);
        Assert.Contains("*.xyz", output); // 无匹配时应列出所有模式
        Assert.Contains("*.zzz", output);
    }

    [Fact]
    public async Task Glob_MissingPattern_Throws()
    {
        var tool = new GlobTool();
        var ctx = MakeContext(_dir);
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(new JsonObject(), ctx, CancellationToken.None));
        Assert.Contains("pattern", ex.Message);
    }

    [Fact]
    public async Task Glob_Results_AreSortedDeterministically()
    {
        // 回归：枚举顺序跨平台不定，输出应按相对路径排序，保证确定性
        File.WriteAllText(Path.Combine(_dir, "zeta.txt"), "");
        File.WriteAllText(Path.Combine(_dir, "alpha.txt"), "");
        File.WriteAllText(Path.Combine(_dir, "mid.txt"), "");
        var tool = new GlobTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "*.txt" }, ctx, CancellationToken.None);
        var idxAlpha = output.IndexOf("alpha.txt", StringComparison.Ordinal);
        var idxMid = output.IndexOf("mid.txt", StringComparison.Ordinal);
        var idxZeta = output.IndexOf("zeta.txt", StringComparison.Ordinal);
        Assert.True(idxAlpha >= 0 && idxMid > idxAlpha && idxZeta > idxMid, $"结果应按字母序排列:\n{output}");
    }
}
