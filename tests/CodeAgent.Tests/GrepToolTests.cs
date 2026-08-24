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
    public async Task Grep_CountOnly_OutputsPerFileCounts()
    {
        // count_only：文件:匹配行数（rg -c 风格）；无命中的文件不出现
        File.WriteAllText(Path.Combine(_dir, "two.cs"), "TODO a\nTODO b\ndone\n");
        File.WriteAllText(Path.Combine(_dir, "one.cs"), "TODO c\n");
        File.WriteAllText(Path.Combine(_dir, "clean.cs"), "nothing\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "todo", ["count_only"] = true }, ctx, CancellationToken.None);

        Assert.Contains("two.cs:2", output);
        Assert.Contains("one.cs:1", output);
        Assert.DoesNotContain("clean.cs", output);
        Assert.Contains("匹配 2 个文件", output); // hits 以文件为粒度
    }

    [Fact]
    public async Task Grep_Multiline_MatchesAcrossLines()
    {
        // 跨行模式：\n 参与匹配（多行 JSON 块）；行号取命中起点
        File.WriteAllText(Path.Combine(_dir, "data.json"),
            "prefix\n{\n  \"key\": 1\n}\nsuffix\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var single = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "\\{\\s*\"key\"" }, ctx, CancellationToken.None);
        Assert.Contains("无匹配", single); // 逐行模式：{ 与 "key" 不同行，匹配不到

        var multi = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "\\{\\s*\"key\"", ["multiline"] = true }, ctx, CancellationToken.None);
        Assert.Contains("匹配 1 处", multi);
        Assert.Contains("data.json:2", multi);   // 命中起点在第 2 行（{ 所在行）
        Assert.Contains("+1|", multi);           // 第二行内容作为续行展示
    }

    [Fact]
    public async Task Grep_Multiline_LongSpan_FoldedWithRangeNotice()
    {
        // 跨 5 行以上的命中：显示前 3 行 + 跨行范围提示
        File.WriteAllText(Path.Combine(_dir, "block.txt"), "A\nB\nC\nD\nE\nF\nG\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "B[\\s\\S]*F", ["multiline"] = true }, ctx, CancellationToken.None);

        Assert.Contains("block.txt:2", output);        // 起点行号
        Assert.Contains("命中跨 2-6 共 5 行", output);   // 范围提示
        Assert.Contains("+1|", output);                 // 续行展示
    }

    [Fact]
    public async Task Grep_Multiline_SingleLineHit_NoFoldNotice()
    {
        // 单行命中（即使 multiline 开着）：与逐行模式相同的展示，无跨行提示
        File.WriteAllText(Path.Combine(_dir, "one.txt"), "alpha\nbeta\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "beta", ["multiline"] = true }, ctx, CancellationToken.None);
        Assert.Contains("one.txt:2: beta", output);
        Assert.DoesNotContain("命中跨", output);
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
    [Fact]
    public async Task Grep_GbkEncodedChineseFile_StillMatches()
    {
        // 回归：老 Windows 工具保存的 ANSI（GBK）中文文件按 UTF-8 读全是替换符，中文 pattern 永远搜不到
        _ = TextUtil.EstimateTokens(""); // 触发 TextUtil 静态构造：注册 GB18030 代码页
        var gbk = System.Text.Encoding.GetEncoding("GB18030");
        File.WriteAllBytes(Path.Combine(_dir, "legacy.txt"), gbk.GetBytes("这是老编码的中文内容\nplain ascii line\n"));
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "老编码" }, ctx, CancellationToken.None);

        Assert.Contains("legacy.txt:1", output);
        Assert.Contains("这是老编码的中文内容", output);
    }

    [Fact]
    public async Task Grep_CaseSensitive_OverridesSmartCase()
    {
        // 智能大小写下全小写 pattern 忽略大小写；case_sensitive=true 应强制精确匹配
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "var id = 1;\nvar ID = 2;\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "id", ["case_sensitive"] = true }, ctx, CancellationToken.None);

        Assert.Contains("a.cs:1", output);
        Assert.DoesNotContain("a.cs:2", output); // ID 不再命中
    }

    [Fact]
    public async Task Grep_Invert_OutputsNonMatchingLines()
    {
        // 类似 rg -v：输出不匹配 pattern 的行，而非匹配的
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "alpha\nBETA\ngamma");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "alpha", ["invert"] = true }, ctx, CancellationToken.None);

        Assert.Contains("a.txt:2", output);
        Assert.Contains("a.txt:3", output);
        Assert.DoesNotContain("a.txt:1", output); // 匹配行被排除
        Assert.Contains("BETA", output);
        Assert.Contains("gamma", output);
    }

    [Fact]
    public async Task Grep_Invert_CountOnly_CountsNonMatching()
    {
        // invert + count_only：统计非匹配行数（无尾随换行，避免空行被计入）
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "keep\nDROP\nkeep2");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "DROP", ["invert"] = true, ["count_only"] = true }, ctx, CancellationToken.None);

        Assert.Contains("a.txt:2", output); // 2 行非匹配（keep / keep2）
    }

    [Fact]
    public async Task Grep_Invert_FilesOnly_MarksFilesWithNonMatchingLine()
    {
        File.WriteAllText(Path.Combine(_dir, "onlymatch.txt"), "DROP\nDROP");
        File.WriteAllText(Path.Combine(_dir, "hasmix.txt"), "DROP\nkeep");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "DROP", ["invert"] = true, ["files_only"] = true }, ctx, CancellationToken.None);

        Assert.Contains("hasmix.txt", output);
        Assert.DoesNotContain("onlymatch.txt", output); // 全是匹配行 → invert 下无命中
    }

    [Fact]
    public async Task Grep_Invert_WithMultiline_Throws()
    {
        // invert 跨行反转无意义，应明确报错而非给出误导结果
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x\ny\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["pattern"] = "x", ["invert"] = true, ["multiline"] = true }, ctx, CancellationToken.None));
        Assert.Contains("invert", ex.Message);
    }

    [Fact]
    public async Task Grep_Word_MatchesWholeWordsOnly()
    {
        // word（rg -w）：cat 不应命中 category，但应命中 "a cat sat" 中的独立 cat
        File.WriteAllText(Path.Combine(_dir, "w.txt"), "category\na cat sat\nconcat\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "cat", ["word"] = true }, ctx, CancellationToken.None);

        Assert.Contains("w.txt:2", output);          // 整词 cat 命中（第 2 行）
        Assert.Contains("a cat sat", output);         // 命中行内容
        Assert.DoesNotContain("w.txt:1", output);     // category（第 1 行）不是命中行
        Assert.DoesNotContain("w.txt:3", output);     // concat（第 3 行）不是命中行
    }

    [Fact]
    public async Task Grep_WithoutWord_MatchesSubstring()
    {
        // 不加 word：默认子串匹配，cat 命中 category
        File.WriteAllText(Path.Combine(_dir, "w2.txt"), "category\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "cat" }, ctx, CancellationToken.None);

        Assert.Contains("category", output);
    }

    [Fact]
    public async Task Grep_SingleFileManyMatches_CappedAtMaxResults()
    {
        // 回归：单个文件内大量命中时，默认 max_results=50 应收敛，并附「可能还有更多」提示
        var lines = Enumerable.Range(1, 100).Select(i => $"needle line {i}");
        File.WriteAllText(Path.Combine(_dir, "many.txt"), string.Join('\n', lines));
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "needle" }, ctx, CancellationToken.None);

        Assert.Contains("已达 max_results", output); // 截断提示
        Assert.Contains("many.txt:1", output);          // 第一个命中行
        Assert.Contains("many.txt:50", output);         // 第 50 个是最后一个命中行
        Assert.DoesNotContain("many.txt:51", output);   // 第 51 行不是命中行（仅可能作为上下文出现）
    }

    [Fact]
    public async Task Grep_MaxResultsZero_ClampsToOne()
    {
        // max_results=0 是无意义输入：应收敛到 1（至少返回首个命中），而非空结果或报错
        File.WriteAllText(Path.Combine(_dir, "z.txt"), "hit\nother\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "hit", ["max_results"] = 0 }, ctx, CancellationToken.None);

        Assert.Contains("z.txt:1", output);   // 首个命中仍返回
        Assert.DoesNotContain("z.txt:2", output); // 其余被截断
    }

    [Fact]
    public async Task Grep_SmartCase_LowercasePattern_IgnoresCase()
    {
        // 智能大小写（ripgrep 风格）的另一面：全小写 pattern 默认忽略大小写，
        // 大写文本也能命中（与 case_sensitive=true 的强制精确匹配互为补充）
        File.WriteAllText(Path.Combine(_dir, "case.txt"), "TODO buy milk\nDone items\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "todo" }, ctx, CancellationToken.None);

        Assert.Contains("case.txt:1", output);
        Assert.Contains("TODO buy milk", output);
        Assert.DoesNotContain("case.txt:2", output); // Done 与 pattern 无关
    }

    [Fact]
    public async Task Grep_FilesOnlyBeatsCountOnly()
    {
        // 同时给 files_only 与 count_only 时，files_only 优先：输出文件名而非「文件:行数」
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "alpha\nbeta\n");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "gamma\n");
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "a", ["files_only"] = true, ["count_only"] = true }, ctx, CancellationToken.None);

        Assert.Contains("a.txt", output);   // 仅列文件名
        Assert.DoesNotContain("a.txt:", output); // 不出现「文件:行数」计数格式
    }

    [Fact]
    public async Task Grep_Context_ClampsToMaxTen()
    {
        // context 超过上限 10 应收敛：距离命中行超过 10 的行不应作为上下文出现
        var lines = Enumerable.Range(1, 25).Select(i => i == 13 ? "TARGET" : $"line{i}");
        File.WriteAllText(Path.Combine(_dir, "ctx.txt"), string.Join('\n', lines));
        var tool = new GrepTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["pattern"] = "TARGET", ["context"] = 100 }, ctx, CancellationToken.None);

        Assert.Contains("ctx.txt:13", output);   // 命中行
        Assert.Contains(" 3| ", output);          // 第 3 行在 context=10 范围内（距离 10）
        Assert.DoesNotContain(" 1| ", output);   // 第 1 行距离 12 > 10，不应出现
        Assert.DoesNotContain(" 24| ", output);  // 第 24 行距离 11 > 10，不应出现
    }
}
