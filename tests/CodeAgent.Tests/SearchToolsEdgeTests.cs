using System;
using System.Collections.Generic;
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
        // 超过 500 个结果时给出截断提示（显示上限 500）
        Directory.CreateDirectory(PathOf("many"));
        for (int i = 0; i < 520; i++)
            File.WriteAllText(PathOf("many", $"f{i:0000}.txt"), "x");
        var args = new JsonObject { ["pattern"] = "many/*.txt" };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("仅显示前 500", result);
    }

    [Fact]
    public async Task Glob_RecursivePattern_FindsNestedFiles()
    {
        // **/*.cs:递归匹配子目录中的文件
        Directory.CreateDirectory(Path.Combine(_dir, "src", "lib"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, "src", "lib", "b.cs"), "x");
        var args = new JsonObject { ["pattern"] = "**/*.cs" };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("src/a.cs", result);
        Assert.Contains("src/lib/b.cs", result);
    }

    [Fact]
    public async Task Glob_HiddenFiles_AreIncluded()
    {
        // 以点开头的文件（如 .gitignore）也应被 glob 匹配
        File.WriteAllText(PathOf(".gitignore"), "x");
        File.WriteAllText(PathOf("normal.txt"), "x");
        var args = new JsonObject { ["pattern"] = ".*", ["show_hidden"] = true };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains(".gitignore", result);
        Assert.DoesNotContain("normal.txt", result);
    }

    [Fact]
    public async Task Glob_NoMatch_ReturnsEmpty()
    {
        // 无匹配时返回空（或提示）
        var args = new JsonObject { ["pattern"] = "*.nonexistent" };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.DoesNotContain(".cs", result); // 无匹配项
    }

    [Fact]
    public async Task Glob_Ignore_SkipsMatchingFiles()
    {
        // ignore:排除匹配 glob 的文件
        File.WriteAllText(PathOf("keep.txt"), "x");
        File.WriteAllText(PathOf("skip.min.js"), "x");
        var args = new JsonObject { ["pattern"] = "*.*", ["ignore"] = "*.min.js" };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("keep.txt", result);
        Assert.DoesNotContain("skip.min.js", result);
    }

    [Fact]
    public async Task Glob_MaxResults_RespectsCap()
    {
        // max_results 控制返回上限；达到上限时提示可能不完整
        Directory.CreateDirectory(PathOf("mr"));
        for (int i = 0; i < 10; i++)
            File.WriteAllText(PathOf("mr", $"f{i}.txt"), "x");
        var args = new JsonObject { ["pattern"] = "mr/*.txt", ["max_results"] = 3 };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length); // 3 个结果 + 1 行截断提示
        Assert.StartsWith("mr/f", result); // 结果以文件路径开头
        Assert.Contains("仅显示前 3", result); // 截断提示
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
        var args = new JsonObject { ["pattern"] = "needle", ["max_line_length"] = 300 };
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

    [Fact]
    public async Task Grep_Word_WithAlternation_MatchesWholeWords()
    {
        // word=true + 含 | 的模式：两侧加 \b 应包裹整个模式
        File.WriteAllText(Path.Combine(_dir, "wwa.txt"), "cat\ncategory\ndog\ndogma\nhotdog\ncatdog\n");
        var args = new JsonObject { ["pattern"] = "cat|dog", ["word"] = true };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("wwa.txt:1: cat", result); // 完整单词
        Assert.Contains("wwa.txt:3: dog", result); // 完整单词
        Assert.DoesNotContain("wwa.txt:2: category", result); // category 不匹配
        Assert.DoesNotContain("wwa.txt:4: dogma", result); // dogma 不匹配
        Assert.DoesNotContain("wwa.txt:5: hotdog", result); // hotdog 不匹配
        Assert.DoesNotContain("wwa.txt:6: catdog", result); // catdog 不匹配
    }

    [Fact]
    public async Task Grep_Word_MatchWholeWordOnly()
    {
        // word=true:只匹配完整单词（cat 不命中 category）
        File.WriteAllText(PathOf("word.txt"), "cat\ncategory\nconcatenate\n");
        var args = new JsonObject { ["pattern"] = "cat", ["word"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("word.txt:1: cat", result);
        Assert.DoesNotContain("word.txt:2: category", result); // category 不应作为匹配行
        Assert.DoesNotContain("word.txt:3: concat", result); // concatenate 不应作为匹配行
    }

    [Fact]
    public async Task Grep_Multiline_MatchesAcrossLines()
    {
        // multiline=true:\n 参与匹配，可跨行搜索块
        File.WriteAllText(PathOf("multi.txt"), "start\nTARGET\nend\nother\n");
        var args = new JsonObject { ["pattern"] = "start\\nTARGET", ["multiline"] = true };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("multi.txt:1: start", result);
        Assert.Contains("  +1| TARGET", result); // 跨行匹配的第 2 行以 +1| 前缀显示
    }

    [Fact]
    public async Task Grep_CountOnly_ReturnsMatchCounts()
    {
        // count_only=true:只返回每个文件的匹配行数
        File.WriteAllText(PathOf("cnt.txt"), "aa\nbb\naa\ncc\naa\n");
        var args = new JsonObject { ["pattern"] = "aa", ["count_only"] = true };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("cnt.txt:3", result); // 3 处匹配
    }

    [Fact]
    public async Task Grep_Exclude_SkipsMatchingFiles()
    {
        // exclude:跳过匹配 glob 的文件
        File.WriteAllText(PathOf("inc.txt"), "target\n");
        File.WriteAllText(PathOf("inc.g.txt"), "target\n");
        var args = new JsonObject { ["pattern"] = "target", ["exclude"] = "*.g.txt" };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("inc.txt:1: target", result);
        Assert.DoesNotContain("inc.g.txt", result);
    }

    [Fact]
    public async Task Grep_FilesOnly_ReturnsOnlyFilenames()
    {
        // files_only=true:只返回匹配的文件名，不返回行内容
        File.WriteAllText(PathOf("fo1.txt"), "target\n");
        File.WriteAllText(PathOf("fo2.txt"), "target\n");
        var args = new JsonObject { ["pattern"] = "target", ["files_only"] = true };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("fo1.txt", result);
        Assert.Contains("fo2.txt", result);
        Assert.DoesNotContain(":target", result); // 无行内容
    }

    [Fact]
    public async Task Grep_CountOnly_ShowsTotalMatches()
    {
        // count_only=true:显示每文件匹配数及总匹配数
        File.WriteAllText(PathOf("c1.txt"), "a\ntarget\nb\ntarget\n");
        File.WriteAllText(PathOf("c2.txt"), "target\ntarget\ntarget\n");
        var args = new JsonObject { ["pattern"] = "target", ["count_only"] = true };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("c1.txt:2", result); // 2 处匹配
        Assert.Contains("c2.txt:3", result); // 3 处匹配
        Assert.Contains("共 5 处匹配", result); // 总计
    }

    [Fact]
    public async Task Glob_Depth_Zero_OnlyRoot()
    {
        // depth=0:只返回根目录匹配的文件
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(PathOf("root.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "sub", "inner.txt"), "x");
        var args = new JsonObject { ["pattern"] = "*.txt", ["depth"] = 0 };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("root.txt", result);
        Assert.DoesNotContain("inner.txt", result); // 不递归
    }

    [Fact]
    public async Task Glob_Depth_One_IncludesImmediateChildren()
    {
        // depth=1:返回根目录 + 直接子目录
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(PathOf("root.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "sub", "inner.txt"), "x");
        var args = new JsonObject { ["pattern"] = "**/*.txt", ["depth"] = 1 };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("root.txt", result);
        Assert.Contains("sub/inner.txt", result);
    }

    [Fact]
    public async Task Grep_Depth_Zero_OnlyRoot()
    {
        // depth=0:只搜索根目录
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(PathOf("root.txt"), "target\n");
        File.WriteAllText(Path.Combine(_dir, "sub", "inner.txt"), "target\n");
        var args = new JsonObject { ["pattern"] = "target", ["depth"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("root.txt:1: target", result);
        Assert.DoesNotContain("inner.txt", result); // 不递归
    }

    [Fact]
    public async Task Grep_MaxLineLength_TruncatesLongMatches()
    {
        // max_line_length=20:超长匹配行被截断并附加 " …"
        File.WriteAllText(PathOf("long.txt"), new string('a', 100) + "\n");
        var args = new JsonObject { ["pattern"] = new string('a', 100), ["max_line_length"] = 20 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains(" …", result); // 截断标记
        Assert.Contains(new string('a', 20), result); // 保留前 20 字符
    }

    [Fact]
    public async Task Grep_MaxLineLength_Zero_DisablesTruncation()
    {
        // max_line_length=0:不截断，返回完整行
        File.WriteAllText(PathOf("full.txt"), new string('b', 100) + "\n");
        var args = new JsonObject { ["pattern"] = new string('b', 100), ["max_line_length"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains(new string('b', 100), result); // 完整行
        Assert.DoesNotContain(" …", result); // 无截断标记
    }

    [Fact]
    public async Task Grep_BinaryFiles_Skip_SkipsBinary()
    {
        // binary_files=skip（默认）:跳过二进制文件
        File.WriteAllBytes(PathOf("bin.dat"), [0x00, 0x01, 0x02, 0x03]);
        var args = new JsonObject { ["pattern"] = "target", ["path"] = "bin.dat" };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("无匹配", result); // 二进制文件被跳过
    }

    [Fact]
    public async Task Grep_BinaryFiles_Text_TreatsAsText()
    {
        // binary_files=text:把二进制文件当作文本搜索（可能产生乱码，但用户显式要求）
        var bytes = new List<byte>();
        foreach (var c in "target")
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(c.ToString()));
        bytes.Add(0x00);
        File.WriteAllBytes(PathOf("bin2.dat"), bytes.ToArray());
        var args = new JsonObject { ["pattern"] = "target", ["path"] = "bin2.dat", ["binary_files"] = "text" };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("target", result); // 找到了文本内容
    }

    [Fact]
    public async Task Grep_ContextBeforeAfter_AsymmetricContext()
    {
        // context_before=1, context_after=2:非对称上下文
        File.WriteAllText(PathOf("asym.txt"), "a\nb\nMATCH\nd\ne\n");
        var args = new JsonObject { ["pattern"] = "MATCH", ["context_before"] = 1, ["context_after"] = 2 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("  2| b", result); // 前 1 行
        Assert.Contains("3: MATCH", result); // 匹配行（格式:行号:内容）
        Assert.Contains("  4| d", result); // 后 2 行
        Assert.Contains("  5| e", result); // 后 2 行
        Assert.DoesNotContain("  1| a", result); // 前 1 行之外的不显示
    }

    [Fact]
    public async Task Grep_ContextBeforeAfter_FallbackToContext()
    {
        // 只给 context_before:context_after 回退到对称 context
        File.WriteAllText(PathOf("asym2.txt"), "a\nb\nMATCH\nd\ne\n");
        var args = new JsonObject { ["pattern"] = "MATCH", ["context"] = 1 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("  2| b", result); // 前 1 行
        Assert.Contains("  4| d", result); // 后 1 行
        Assert.DoesNotContain("  1| a", result); // 前 1 行之外
        Assert.DoesNotContain("  5| e", result); // 后 1 行之外
    }

    [Fact]
    public async Task Grep_LineNumber_False_OmitsLineNumbers()
    {
        // line_number=false:输出格式为 file: content，去掉行号前缀
        File.WriteAllText(PathOf("ln.txt"), "alpha\nbeta\ngamma\n");
        var args = new JsonObject { ["pattern"] = "beta", ["line_number"] = false };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("ln.txt: beta", result); // 无行号
        Assert.DoesNotContain("ln.txt:2:", result); // 不显示行号
    }

    [Fact]
    public async Task Grep_LineNumber_True_IncludesLineNumbers()
    {
        // line_number=true（默认）:输出格式为 file:line: content
        File.WriteAllText(PathOf("ln2.txt"), "alpha\nbeta\ngamma\n");
        var args = new JsonObject { ["pattern"] = "beta", ["line_number"] = true };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("ln2.txt:2: beta", result); // 显示行号
    }

    [Fact]
    public async Task Glob_SortByName_IsAlphabetical()
    {
        // sort_by=name:结果按路径字母序排列
        Directory.CreateDirectory(Path.Combine(_dir, "zeta"));
        File.WriteAllText(PathOf("a.txt"), "x");
        File.WriteAllText(PathOf("z.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "zeta", "m.txt"), "x");
        var args = new JsonObject { ["pattern"] = "**/*.txt", ["sort_by"] = "name" };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        var lines = result.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Equal("a.txt", lines[0]);
        Assert.Equal("z.txt", lines[1]);
        Assert.Equal("zeta/m.txt", lines[2]);
    }

    [Fact]
    public async Task Glob_SortBySize_LargestFirst()
    {
        // sort_by=size:结果按文件大小降序排列
        File.WriteAllText(PathOf("small.txt"), "x");
        File.WriteAllText(PathOf("large.txt"), new string('a', 1000));
        var args = new JsonObject { ["pattern"] = "*.txt", ["sort_by"] = "size" };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        var lines = result.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Equal("large.txt", lines[0]);
        Assert.Equal("small.txt", lines[1]);
    }

    [Fact]
    public async Task Glob_IncludeIgnored_False_SkipsIgnoredDirs()
    {
        // include_ignored=false（默认）:跳过 .git 等被忽略目录
        Directory.CreateDirectory(Path.Combine(_dir, ".git", "objects"));
        File.WriteAllText(Path.Combine(_dir, ".git", "config"), "x");
        File.WriteAllText(PathOf("normal.txt"), "x");
        var args = new JsonObject { ["pattern"] = "**/*.txt", ["include_ignored"] = false };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("normal.txt", result);
        Assert.DoesNotContain(".git/config", result); // 被忽略目录被跳过
    }

    [Fact]
    public async Task Glob_IncludeIgnored_True_SearchesIgnoredDirs()
    {
        // include_ignored=true:搜索 .git 等被忽略目录内的文件
        Directory.CreateDirectory(Path.Combine(_dir, ".git", "objects"));
        File.WriteAllText(Path.Combine(_dir, ".git", "config"), "[core]");
        File.WriteAllText(PathOf("normal.txt"), "x");
        var args = new JsonObject { ["pattern"] = "**/*", ["include_ignored"] = true };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains(".git/config", result); // 被忽略目录内的文件可见
    }

    [Fact]
    public async Task Grep_IncludeIgnored_False_SkipsIgnoredDirs()
    {
        // include_ignored=false（默认）:跳过 .git 等被忽略目录
        Directory.CreateDirectory(Path.Combine(_dir, ".git", "objects"));
        File.WriteAllText(Path.Combine(_dir, ".git", "config"), "secret");
        File.WriteAllText(PathOf("normal.txt"), "secret");
        var args = new JsonObject { ["pattern"] = "secret", ["include_ignored"] = false };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("normal.txt", result);
        Assert.DoesNotContain(".git/config", result); // 被忽略目录被跳过
    }

    [Fact]
    public async Task Grep_IncludeIgnored_True_SearchesIgnoredDirs()
    {
        // include_ignored=true:搜索 .git 等被忽略目录内的文件
        Directory.CreateDirectory(Path.Combine(_dir, ".git", "objects"));
        File.WriteAllText(Path.Combine(_dir, ".git", "config"), "secret");
        File.WriteAllText(PathOf("normal.txt"), "visible");
        var args = new JsonObject { ["pattern"] = "secret", ["include_ignored"] = true };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains(".git/config", result); // 被忽略目录内的文件可见
    }

    [Fact]
    public async Task Grep_Heading_True_GroupsMatchesByFile()
    {
        // heading=true:每个文件的匹配前先输出文件路径
        File.WriteAllText(PathOf("h1.txt"), "alpha\nbeta\ngamma\n");
        File.WriteAllText(PathOf("h2.txt"), "delta\nepsilon\nbeta\n");
        var args = new JsonObject { ["pattern"] = "beta", ["heading"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("h1.txt", result); // 文件路径作为标题出现
        Assert.Contains("h2.txt", result); // 第二个文件路径也出现
        Assert.Contains("h1.txt:2: beta", result); // 匹配行仍含文件路径
        Assert.Contains("h2.txt:3: beta", result);
    }

    [Fact]
    public async Task Grep_MaxMatchesPerFile_LimitsOutputPerFile()
    {
        // max_matches_per_file=1:每个文件最多显示 1 处匹配
        File.WriteAllText(PathOf("mp.txt"), "a\nbeta\nc\nbeta\ne\nbeta\n");
        var args = new JsonObject { ["pattern"] = "beta", ["max_matches_per_file"] = 1, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        var betaCount = result.Split('\n').Count(l => l.Contains("beta"));
        Assert.Equal(1, betaCount); // 仅显示 1 处匹配
    }

    [Fact]
    public async Task Grep_OutputMode_Content_ReturnsOnlyMatches()
    {
        // output_mode=content:匹配文本前无文件路径和行号前缀
        File.WriteAllText(PathOf("om.txt"), "alpha\nbeta\ngamma\n");
        var args = new JsonObject { ["pattern"] = "beta", ["output_mode"] = "content", ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("beta", result); // 匹配文本存在
        Assert.DoesNotContain("om.txt:", result); // 无文件路径前缀
        Assert.DoesNotContain("2:", result); // 无行号前缀
    }

    [Fact]
    public async Task Grep_OutputMode_ContentWithoutFilename_ReturnsLineAndContent()
    {
        // output_mode=content_without_filename:输出行号: 内容，无文件路径
        File.WriteAllText(PathOf("om2.txt"), "alpha\nbeta\ngamma\n");
        var args = new JsonObject { ["pattern"] = "beta", ["output_mode"] = "content_without_filename" };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("2: beta", result);
        Assert.DoesNotContain("om2.txt", result); // 无文件路径
    }

    [Fact]
    public async Task Grep_OutputMode_ContentWithoutLineNumber_ReturnsFileAndContent()
    {
        // output_mode=content_without_line_number:输出文件: 内容，无行号
        File.WriteAllText(PathOf("om3.txt"), "alpha\nbeta\ngamma\n");
        var args = new JsonObject { ["pattern"] = "beta", ["output_mode"] = "content_without_line_number" };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("om3.txt: beta", result);
        Assert.DoesNotContain("om3.txt:2:", result); // 无行号
    }

    [Fact]
    public async Task Grep_Literal_True_TreatsPatternAsLiteralString()
    {
        // literal=true:pattern 中的正则特殊字符被转义，当作普通字符串搜索
        File.WriteAllText(PathOf("lit.txt"), "a.b\naab\nacb\n");
        var args = new JsonObject { ["pattern"] = "a.b", ["literal"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("a.b", result); // 字面量匹配 "a.b"
        Assert.DoesNotContain("aab", result); // 不匹配 "aab"（正则 "." 会匹配任意字符）
        Assert.DoesNotContain("acb", result); // 不匹配 "acb"
    }

    [Fact]
    public async Task Grep_Literal_False_RegexPatternMatches()
    {
        // literal=false（默认）:"a.b" 作为正则匹配 a 任意 b（如 aab、acb、a.b）
        File.WriteAllText(PathOf("lit2.txt"), "a.b\naab\nacb\n");
        var args = new JsonObject { ["pattern"] = "a.b", ["literal"] = false, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("a.b", result); // 正则匹配
        Assert.Contains("aab", result); // "." 匹配任意字符
        Assert.Contains("acb", result); // "." 匹配任意字符
    }

    [Fact]
    public async Task Grep_Stats_True_ShowsSearchStatistics()
    {
        // stats=true:输出末尾附加搜索统计信息
        File.WriteAllText(PathOf("st.txt"), "alpha\nbeta\ngamma\n");
        File.WriteAllText(PathOf("st2.txt"), "delta\nepsilon\n");
        var args = new JsonObject { ["pattern"] = "beta", ["stats"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("beta", result); // 匹配结果存在
        Assert.Contains("[stats]", result); // 统计信息存在
        Assert.Contains("扫描", result); // 包含扫描文件数
    }

    [Fact]
    public async Task Grep_Stats_False_NoStatistics()
    {
        // stats=false（默认）:不显示搜索统计
        File.WriteAllText(PathOf("st3.txt"), "alpha\nbeta\n");
        var args = new JsonObject { ["pattern"] = "beta", ["stats"] = false, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("beta", result);
        Assert.DoesNotContain("[stats]", result); // 无统计信息
    }

    [Fact]
    public async Task Glob_MaxResults_RespectsDisplayLimit()
    {
        // max_results=5:收集 5 个结果，显示 5 个结果 + 截断提示
        for (int i = 0; i < 10; i++)
            File.WriteAllText(PathOf($"g{i}.txt"), $"content{i}");
        var args = new JsonObject { ["pattern"] = "*.txt", ["max_results"] = 5 };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        Assert.Equal(6, lines.Count); // 5 个结果 + 1 行截断提示
        Assert.DoesNotContain("仅显示前 300", result); // 不再硬编码 300
    }

    [Fact]
    public async Task Glob_MaxResults_OverDisplayCap_TruncatesTo500()
    {
        // max_results=1000:收集上限 1000，但显示被截断到 500
        for (int i = 0; i < 10; i++)
            File.WriteAllText(PathOf($"h{i}.txt"), $"content{i}");
        var args = new JsonObject { ["pattern"] = "*.txt", ["max_results"] = 1000 };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        Assert.Equal(10, lines.Count); // 只有 10 个文件，全部显示
        Assert.DoesNotContain("仅显示前 300", result); // 不再硬编码 300
    }

    [Fact]
    public async Task Glob_ShowModified_True_DisplaysFileModifiedTime()
    {
        // show_modified=true:文件路径后附加修改时间
        File.WriteAllText(PathOf("sm.txt"), "hello");
        var args = new JsonObject { ["pattern"] = "sm.txt", ["show_modified"] = true };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("sm.txt (", result); // 时间格式：yyyy-MM-dd HH:mm
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", result); // 匹配时间格式
    }

    [Fact]
    public async Task Glob_ShowModified_False_NoTimeInfo()
    {
        // show_modified=false（默认）:不显示修改时间
        File.WriteAllText(PathOf("sm2.txt"), "hello");
        var args = new JsonObject { ["pattern"] = "sm2.txt", ["show_modified"] = false };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("sm2.txt", result);
        Assert.DoesNotContain("(", result); // 无时间括号
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowModified_True_DisplaysFileModifiedTime()
    {
        // show_modified=true + files_only=true:文件路径后附加修改时间
        File.WriteAllText(PathOf("gm.txt"), "alpha\nbeta\ngamma\n");
        var args = new JsonObject { ["pattern"] = "beta", ["files_only"] = true, ["show_modified"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gm.txt (", result); // 时间格式：yyyy-MM-dd HH:mm
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", result); // 匹配时间格式
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowModified_False_NoTimeInfo()
    {
        // show_modified=false（默认）:不显示修改时间
        File.WriteAllText(PathOf("gm2.txt"), "alpha\nbeta\n");
        var args = new JsonObject { ["pattern"] = "beta", ["files_only"] = true, ["show_modified"] = false, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gm2.txt", result);
        Assert.DoesNotContain("(", result); // 无时间括号
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowEncoding_True_DisplaysEncodingInfo()
    {
        // show_encoding=true + files_only=true:文件路径后附加编码信息
        File.WriteAllText(PathOf("ge.txt"), "alpha\nbeta\n");
        var args = new JsonObject { ["pattern"] = "beta", ["files_only"] = true, ["show_encoding"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("ge.txt [UTF-8]", result); // 显示 UTF-8 编码
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowEncoding_False_NoEncodingInfo()
    {
        // show_encoding=false（默认）:不显示编码信息
        File.WriteAllText(PathOf("ge2.txt"), "alpha\nbeta\n");
        var args = new JsonObject { ["pattern"] = "beta", ["files_only"] = true, ["show_encoding"] = false, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("ge2.txt", result);
        Assert.DoesNotContain("[", result); // 无编码括号
    }

    [Fact]
    public async Task Glob_ShowSize_True_DisplaysFileSize()
    {
        // show_size=true:文件路径后附加文件大小
        File.WriteAllText(PathOf("gs.txt"), new string('a', 2048));
        var args = new JsonObject { ["pattern"] = "gs.txt", ["show_size"] = true };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gs.txt (", result); // 大小格式：xxx B/KB/MB
        Assert.Contains("2.0 KB", result); // 2048 字节 = 2.0 KB
    }

    [Fact]
    public async Task Glob_ShowSize_False_NoSizeInfo()
    {
        // show_size=false（默认）:不显示文件大小
        File.WriteAllText(PathOf("gs2.txt"), new string('a', 100));
        var args = new JsonObject { ["pattern"] = "gs2.txt", ["show_size"] = false };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gs2.txt", result);
        Assert.DoesNotContain("(", result); // 无大小括号
    }

    [Fact]
    public async Task Grep_ShowTotalMatches_True_DisplaysTotalMatchCount()
    {
        // show_total_matches=true:输出末尾显示总匹配数
        File.WriteAllText(PathOf("tm.txt"), "alpha beta\ngamma delta\nalpha gamma\n");
        var args = new JsonObject { ["pattern"] = "alpha", ["show_total_matches"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("[total] 共 2 处匹配", result); // 2 行包含 alpha
    }

    [Fact]
    public async Task Grep_ShowTotalMatches_False_NoTotalMatchCount()
    {
        // show_total_matches=false（默认）:不显示总匹配数
        File.WriteAllText(PathOf("tm2.txt"), "alpha beta\ngamma delta\n");
        var args = new JsonObject { ["pattern"] = "alpha", ["show_total_matches"] = false, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("alpha", result);
        Assert.DoesNotContain("[total]", result); // 无总匹配数
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowSize_True_DisplaysFileSize()
    {
        // show_size=true + files_only=true:文件路径后附加文件大小
        File.WriteAllText(PathOf("gs.txt"), new string('a', 2048));
        var args = new JsonObject { ["pattern"] = "a", ["files_only"] = true, ["show_size"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gs.txt (", result); // 大小格式：xxx B/KB/MB
        Assert.Contains("2.0 KB", result); // 2048 字节 = 2.0 KB
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowSize_False_NoSizeInfo()
    {
        // show_size=false（默认）:不显示文件大小
        File.WriteAllText(PathOf("gs2.txt"), new string('a', 100));
        var args = new JsonObject { ["pattern"] = "a", ["files_only"] = true, ["show_size"] = false, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gs2.txt", result);
        Assert.DoesNotContain("(", result); // 无大小括号
    }

    [Fact]
    public async Task Glob_ShowByteCount_True_DisplaysByteCount()
    {
        // show_byte_count=true:文件路径后附加精确字节数
        File.WriteAllText(PathOf("gbc.txt"), new string('a', 1536));
        var args = new JsonObject { ["pattern"] = "gbc.txt", ["show_byte_count"] = true };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gbc.txt (", result); // 字节数格式
        Assert.Contains("1,536 bytes", result); // 1536 字节
    }

    [Fact]
    public async Task Glob_ShowByteCount_False_NoByteCountInfo()
    {
        // show_byte_count=false（默认）:不显示字节数
        File.WriteAllText(PathOf("gbc2.txt"), new string('a', 100));
        var args = new JsonObject { ["pattern"] = "gbc2.txt", ["show_byte_count"] = false };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gbc2.txt", result);
        Assert.DoesNotContain("bytes", result); // 无字节数信息
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowWordCount_True_DisplaysWordCount()
    {
        // show_word_count=true + files_only=true:文件路径后附加单词数
        File.WriteAllText(PathOf("gwc.txt"), "alpha beta gamma\n");
        var args = new JsonObject { ["pattern"] = "alpha", ["files_only"] = true, ["show_word_count"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gwc.txt [", result); // 单词数格式
        Assert.Contains("words]", result); // 包含 words 标记
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowWordCount_False_NoWordCountInfo()
    {
        // show_word_count=false（默认）:不显示单词数
        File.WriteAllText(PathOf("gwc2.txt"), "alpha beta\n");
        var args = new JsonObject { ["pattern"] = "alpha", ["files_only"] = true, ["show_word_count"] = false, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gwc2.txt", result);
        Assert.DoesNotContain("words", result); // 无单词数信息
    }

    [Fact]
    public async Task Glob_ShowHash_True_DisplaysShortHash()
    {
        // show_hash=true:文件路径后附加 SHA256 哈希（前 8 位）
        File.WriteAllText(PathOf("gh.txt"), "hello hash\n");
        var args = new JsonObject { ["pattern"] = "gh.txt", ["show_hash"] = true };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gh.txt (sha256:", result); // 哈希格式
        Assert.Matches(@"sha256:[0-9a-fA-F]{8}\.\.\.", result); // 匹配前 8 位十六进制 + ...
    }

    [Fact]
    public async Task Glob_ShowHash_False_NoHashInfo()
    {
        // show_hash=false（默认）:不显示哈希
        File.WriteAllText(PathOf("gh2.txt"), "hello hash\n");
        var args = new JsonObject { ["pattern"] = "gh2.txt", ["show_hash"] = false };
        var result = await new GlobTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gh2.txt", result);
        Assert.DoesNotContain("sha256", result); // 无哈希信息
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowHash_True_DisplaysShortHash()
    {
        // show_hash=true + files_only=true:文件路径后附加 SHA256 哈希（前 8 位）
        File.WriteAllText(PathOf("gh.txt"), "alpha beta\n");
        var args = new JsonObject { ["pattern"] = "alpha", ["files_only"] = true, ["show_hash"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gh.txt (sha256:", result); // 哈希格式
        Assert.Matches(@"sha256:[0-9a-fA-F]{8}\.\.\.", result); // 匹配前 8 位十六进制 + ...
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowHash_False_NoHashInfo()
    {
        // show_hash=false（默认）:不显示哈希
        File.WriteAllText(PathOf("gh2.txt"), "alpha beta\n");
        var args = new JsonObject { ["pattern"] = "alpha", ["files_only"] = true, ["show_hash"] = false, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gh2.txt", result);
        Assert.DoesNotContain("sha256", result); // 无哈希信息
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowLineCount_True_DisplaysLineCount()
    {
        // show_line_count=true + files_only=true:文件路径后附加行数
        File.WriteAllText(PathOf("glc.txt"), "line1\nline2\nline3\n");
        var args = new JsonObject { ["pattern"] = "line", ["files_only"] = true, ["show_line_count"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("glc.txt [", result); // 行数格式
        Assert.Contains("lines]", result); // 包含 lines 标记
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowLineCount_False_NoLineCountInfo()
    {
        // show_line_count=false（默认）:不显示行数
        File.WriteAllText(PathOf("glc2.txt"), "line1\nline2\n");
        var args = new JsonObject { ["pattern"] = "line", ["files_only"] = true, ["show_line_count"] = false, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("glc2.txt", result);
        Assert.DoesNotContain("lines", result); // 无行数信息
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowTotalSize_True_DisplaysTotalSize()
    {
        // show_total_size=true + files_only=true:输出末尾显示匹配文件总大小
        File.WriteAllText(PathOf("gts1.txt"), new string('a', 1024));
        File.WriteAllText(PathOf("gts2.txt"), new string('b', 2048));
        var args = new JsonObject { ["pattern"] = "a", ["files_only"] = true, ["show_total_size"] = true, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("[size] 匹配文件总大小:", result); // 总大小标记
        Assert.Contains("1.0 KB", result); // 1024 字节 = 1.0 KB
    }

    [Fact]
    public async Task Grep_FilesOnly_ShowTotalSize_False_NoTotalSizeInfo()
    {
        // show_total_size=false（默认）:不显示总大小
        File.WriteAllText(PathOf("gts3.txt"), new string('a', 100));
        var args = new JsonObject { ["pattern"] = "a", ["files_only"] = true, ["show_total_size"] = false, ["context"] = 0 };
        var result = await new GrepTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("gts3.txt", result);
        Assert.DoesNotContain("[size]", result); // 无总大小信息
    }

}
