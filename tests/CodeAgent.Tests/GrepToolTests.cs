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
    public async Task Grep_Include_BarePattern_MatchesSubdirectories()
    {
        // 回归：无分隔符的 include（如 "*.cs"）应匹配任意深度（与 ripgrep --glob 一致），
        // 原先裸 glob 只匹配根目录文件，子目录里的会被漏掉
        Directory.CreateDirectory(Path.Combine(_dir, "src", "deep"));
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "TODO root\n");
        File.WriteAllText(Path.Combine(_dir, "src", "deep", "b.cs"), "TODO deep\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "TODO", ["include"] = "*.cs" }, ctx, CancellationToken.None);
        Assert.Contains("a.cs:1", output);
        Assert.Contains("src/deep/b.cs:1", output);
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

    [Fact]
    public async Task Grep_Results_AreSortedByFile()
    {
        // 回归：目录扫描按文件路径排序输出，跨平台确定性
        File.WriteAllText(Path.Combine(_dir, "zeta.cs"), "TODO z\n");
        File.WriteAllText(Path.Combine(_dir, "alpha.cs"), "TODO a\n");
        File.WriteAllText(Path.Combine(_dir, "mid.cs"), "TODO m\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "TODO" }, ctx, CancellationToken.None);
        var idxAlpha = output.IndexOf("alpha.cs:1", StringComparison.Ordinal);
        var idxMid = output.IndexOf("mid.cs:1", StringComparison.Ordinal);
        var idxZeta = output.IndexOf("zeta.cs:1", StringComparison.Ordinal);
        Assert.True(idxAlpha >= 0 && idxMid > idxAlpha && idxZeta > idxMid, $"结果应按文件名排序:\n{output}");
    }

    [Fact]
    public async Task Grep_ContextLines_SharedBetweenNearbyMatches_NotDuplicated()
    {
        // 回归：两个匹配行相距 ≤ 2×context 时，中间的上下文行曾在两个匹配里各输出一次；
        // 现在共享的上下文行只输出一次（避免重复刷屏）
        File.WriteAllText(Path.Combine(_dir, "ctx.cs"),
            "line1\nline2\nmatchA\nline4\nmatchB\nline6\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "match", ["context"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("matchA", output);
        Assert.Contains("matchB", output);
        // 中间的共享行 line4 只应出现一次（作为上下文行），而不是每个匹配各一次
        var count = output.Split('\n').Count(l => l.Contains("4| line4"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Grep_MaxResultsReached_ShowsTruncationNotice()
    {
        // 回归：命中数达到上限时曾静默截断，模型可能误以为已穷尽全部匹配
        File.WriteAllText(Path.Combine(_dir, "g.txt"), string.Join("\n", Enumerable.Range(0, 30).Select(i => $"hit{i}")));
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var out1 = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "hit", ["max_results"] = 5 }, ctx, CancellationToken.None);
        Assert.Contains("max_results=5", out1);
        Assert.Contains("max_results=5", out1);
        Assert.Contains("hit4", out1);
        Assert.DoesNotContain("g.txt:6:", out1); // 上限外只有上下文行可见，无「第 6 条匹配」行

        // 未达上限：无提示
        var out2 = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "hit", ["max_results"] = 50 }, ctx, CancellationToken.None);
        Assert.DoesNotContain("max_results", out2);
    }
}
