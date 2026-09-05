using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>SearchTools(glob/grep)的边界测试(补充 GlobToolTests / GrepToolTests 未覆盖的场景)。</summary>
public class SearchToolsEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-search-" + Guid.NewGuid().ToString("N"));

    public SearchToolsEdgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private static AgentContext MakeContext(string dir) => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(dir),
    };

    private string PathOf(string rel) => Path.Combine(_dir, rel);

    private string PathOf(string a, string b) => Path.Combine(_dir, a, b);

    // ===== glob =====

    [Fact]
    public async Task Glob_MissingDirectory_Throws()
    {
        var args = new JsonObject { ["pattern"] = "*.cs", ["path"] = "no-such-dir" };
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None));
        Assert.Contains("目录不存在", ex.Message);
    }

    [Fact]
    public async Task Glob_EmptyPattern_ThrowsAsMissing()
    {
        // 空 pattern 被视为缺少参数（无意义的模式直接报错，而非空搜）
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new GlobTool().ExecuteAsync(new JsonObject { ["pattern"] = "" }, MakeContext(_dir), CancellationToken.None));
        Assert.Contains("pattern", ex.Message);
    }

    [Fact]
    public async Task Glob_PathArg_ScansSubdirectory()
    {
        Directory.CreateDirectory(PathOf("src"));
        File.WriteAllText(PathOf("src", "b.cs"), "x");
        File.WriteAllText(PathOf("root.cs"), "x");
        var args = new JsonObject { ["pattern"] = "*.cs", ["path"] = "src" };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("b.cs", result);
        Assert.DoesNotContain("root.cs", result);
    }

    [Fact]
    public async Task Glob_ManyResults_TruncationNotice()
    {
        // 超过 300 个结果时给出截断提示（用多目录规避枚举上限）
        Directory.CreateDirectory(PathOf("many"));
        for (int i = 0; i < 320; i++)
            File.WriteAllText(PathOf("many", $"f{i:0000}.txt"), "x");
        var args = new JsonObject { ["pattern"] = "many/*.txt" };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("仅显示前 300", result);
    }

    [Fact]
    public async Task Glob_MaxResults_RespectsCap()
    {
        // max_results 控制返回上限（默认 500）；超限时提示可能不完整
        Directory.CreateDirectory(PathOf("mr"));
        for (int i = 0; i < 10; i++)
            File.WriteAllText(PathOf("mr", $"f{i}.txt"), "x");
        var args = new JsonObject { ["pattern"] = "mr/*.txt", ["max_results"] = 3 };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // 恰好 max_results=3 个文件
        Assert.DoesNotContain("f3.txt", result); // 第 4 个被截断
    }

    // ===== grep =====

    [Fact]
    public async Task Grep_MissingPattern_Throws()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new GrepTool().ExecuteAsync(new JsonObject(), MakeContext(_dir), CancellationToken.None));
        Assert.Contains("pattern", ex.Message);
    }

    [Fact]
    public async Task Grep_InvalidRegex_Throws()
    {
        File.WriteAllText(PathOf("r.txt"), "abc");
        var args = new JsonObject { ["pattern"] = "([unclosed" };
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None));
        Assert.Contains("正则表达式无效", ex.Message);
    }

    [Fact]
    public async Task Grep_NoMatch_ReportsPattern()
    {
        File.WriteAllText(PathOf("r.txt"), "abc");
        var args = new JsonObject { ["pattern"] = "zzz" };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("无匹配", result);
    }

    [Fact]
    public async Task Grep_PathNotExists_Throws()
    {
        var args = new JsonObject { ["pattern"] = "x", ["path"] = "missing-dir" };
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None));
        Assert.Contains("路径不存在", ex.Message);
    }

    [Fact]
    public async Task Grep_SingleFileTarget_SearchesThatFile()
    {
        File.WriteAllText(PathOf("only.txt"), "hello world\nother\n");
        File.WriteAllText(PathOf("other.txt"), "hello world\n");
        var args = new JsonObject { ["pattern"] = "hello", ["path"] = "only.txt" };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("only.txt:1", result);
        Assert.DoesNotContain("other.txt", result); // 单文件目标不扫其他文件
    }

    [Fact]
    public async Task Grep_UppercasePattern_IsCaseSensitive()
    {
        File.WriteAllText(PathOf("cs.txt"), "Hello\nhello\nHELLO\n");
        var args = new JsonObject { ["pattern"] = "HELLO" }; // 含大写 → 区分大小写
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains(":3:", result); // 仅第 3 行 HELLO
        Assert.DoesNotContain(":1:", result);
        Assert.DoesNotContain(":2:", result);
    }

    [Fact]
    public async Task Grep_LowercasePattern_IsCaseInsensitive()
    {
        File.WriteAllText(PathOf("ci.txt"), "Hello\nhello\n");
        var args = new JsonObject { ["pattern"] = "hello" }; // 全小写 → 忽略大小写
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains(":1:", result); // Hello（忽略大小写匹配）
        Assert.Contains(":2:", result); // hello
    }

    [Fact]
    public async Task Grep_ContextZero_NoContextLines()
    {
        File.WriteAllText(PathOf("ctx.txt"), "a\nneedle\nb\n");
        var args = new JsonObject { ["pattern"] = "needle", ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("ctx.txt:2: needle", result);
        Assert.DoesNotContain("| a", result); // 无上下文行
        Assert.DoesNotContain("| b", result);
    }

    [Fact]
    public async Task Grep_MaxResults_IsClamped()
    {
        // 多行匹配 + 小 max_results：输出行数受限于上限
        File.WriteAllText(PathOf("cap.txt"), string.Join('\n', System.Linq.Enumerable.Range(0, 30).Select(i => $"hit{i}")));
        var args = new JsonObject { ["pattern"] = "^hit", ["max_results"] = 5 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("匹配 5 处", result);
        Assert.DoesNotContain("hit9", result);
    }

    [Fact]
    public async Task Grep_BinaryFile_IsSkipped()
    {
        File.WriteAllText(PathOf("bin.dat"), "ab\u0000cd");
        File.WriteAllText(PathOf("ok.txt"), "needle here");
        var args = new JsonObject { ["pattern"] = "needle" };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("ok.txt", result);
        Assert.DoesNotContain("bin.dat", result); // 二进制跳过
    }

    [Fact]
    public async Task Grep_LongLine_IsTruncated()
    {
        var longLine = new string('x', 500) + "needle";
        File.WriteAllText(PathOf("long.txt"), longLine);
        var args = new JsonObject { ["pattern"] = "needle" };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("…", result); // 超长行被截断提示
        Assert.DoesNotContain(new string('x', 400), result); // 未输出完整超长行
    }

    [Fact]
    public async Task Grep_IncludeBarePattern_AppliesAtAnyDepth()
    {
        Directory.CreateDirectory(PathOf("sub"));
        File.WriteAllText(PathOf("top.txt"), "findme");
        File.WriteAllText(PathOf("sub", "deep.txt"), "findme");
        var args = new JsonObject { ["pattern"] = "findme", ["include"] = "*.txt" };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("top.txt", result);
        Assert.Contains("sub/deep.txt", result); // 裸 glob 匹配任意深度
    }

    [Fact]
    public async Task Grep_Invert_ExcludesMatchingLines()
    {
        // invert=true（rg -v）：输出不匹配 pattern 的行；context 行里仍会出现被排除的关键字
        File.WriteAllText(PathOf("inv.txt"), "keep\nskip\nkeep\n");
        var args = new JsonObject { ["pattern"] = "skip", ["invert"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("inv.txt:1: keep", result);
        Assert.Contains("inv.txt:3: keep", result);
        Assert.DoesNotContain("inv.txt:2:", result); // 第 2 行不输出
    }
}
