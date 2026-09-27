using System;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/find</c> 命中片段的**高亮**。
///
/// 此前只说"这个会话命中了"，**不说命中在哪**：搜 "the" 得到三行长得一模一样的
/// <c>[tool] …Enter ColorThemeTests.cs Enter…</c>，用户无从判断该回看哪个会话。
/// 搜索的全部意义就是定位到那一处。
/// </summary>
public sealed class SearchHighlightTests
{
    [Fact]
    public void MatchIsWrapped()
    {
        Assert.Equal("fix «bug» now", Program.HighlightMatches("fix bug now", "bug"));
    }

    [Fact]
    public void AllOccurrencesAreWrapped()
    {
        // 注意语义：这是**子串**匹配，命中词出现在别的词内部也会被包起来
        //（"the" 会命中 Path|The|scape）。词边界匹配是另一件事，本函数不负责。
        Assert.Equal("«fix» the «fix»", Program.HighlightMatches("fix the fix", "fix"));
    }

    [Fact]
    public void MatchInsideAnotherWordIsStillWrapped()
    {
        // 把当前语义钉住，将来有人改成词边界匹配时这条会提醒他这是个行为变更
        Assert.Equal("Pa«th»", Program.HighlightMatches("Path", "th"));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveButCaseIsPreserved()
    {
        // 保留原文大小写：把 The 改成 the 会让用户核对不上原文
        Assert.Equal("«The»", Program.HighlightMatches("The", "the"));
        Assert.Equal("«the»", Program.HighlightMatches("the", "THE"));
    }

    [Fact]
    public void NoMatch_LeavesTextAlone()
    {
        Assert.Equal("nothing here", Program.HighlightMatches("nothing here", "zzz"));
    }

    [Fact]
    public void NullOrEmptyKeyword_LeavesTextAlone()
    {
        Assert.Equal("abc", Program.HighlightMatches("abc", null));
        Assert.Equal("abc", Program.HighlightMatches("abc", ""));
    }

    [Fact]
    public void EmptyText_IsHandled()
    {
        Assert.Equal(string.Empty, Program.HighlightMatches("", "bug"));
    }

    [Fact]
    public void AdjacentMatchesDoNotBreak()
    {
        // 贪婪/惰性都可能在这里出错：连续命中必须各自成对
        var outp = Program.HighlightMatches("aaaa", "aa");
        Assert.Equal("«aa»«aa»", outp);
        Assert.Equal(outp.Count(ch => ch == Program.MatchOpen[0]), outp.Count(ch => ch == Program.MatchClose[0]));
    }

    [Fact]
    public void RenderedLine_ShowsWhereItMatched()
    {
        var line = Program.FormatSearchHitLine("tool", "…Enter ColorThemeTests.cs Enter…", 120, "theme");
        Assert.Contains("«Theme»", line);
    }

    [Fact]
    public void RenderedLine_WithoutKeywordIsUnchanged()
    {
        // 旧调用点（不传关键字）不能受影响
        var line = Program.FormatSearchHitLine("tool", "…ColorThemeTests.cs…", 120);
        Assert.DoesNotContain("«", line);
    }

    [Fact]
    public void MarkerIsNeverSplitByTruncation()
    {
        // 先包标记再按列裁剪会从标记中间劈开，留下孤零零的 «——比没有标记更难读。
        // 因此必须**先裁剪后包标记**：这里把宽度压到让匹配正好落在窗口外/边缘。
        for (var w = 20; w <= 140; w++)
        {
            var line = Program.FormatSearchHitLine("assistant", new string('a', 60) + "NEEDLE" + new string('b', 60), w, "NEEDLE");
            var open = line.Count(ch => ch == Program.MatchOpen[0]);
            var close = line.Count(ch => ch == Program.MatchClose[0]);
            Assert.True(open == close, $"宽度 {w} 下标记不成对：open={open} close={close}\n{line}");
        }
    }

    [Fact]
    public void MarkerSurvivesEveryWidth()
    {
        // 窄到放不下标记时，宁可整段不标记，也不要留半个
        for (var w = 4; w <= 140; w++)
        {
            var line = Program.FormatSearchHitLine("user", "NEEDLE", w, "NEEDLE");
            Assert.Equal(line.Count(ch => ch == Program.MatchOpen[0]), line.Count(ch => ch == Program.MatchClose[0]));
            Assert.True(TextUtil.DisplayWidth(line) <= w || TextUtil.DisplayWidth(line) <= w + 1, $"宽度 {w}");
        }
    }

    [Fact]
    public void FindPassesTheKeywordThrough()
    {
        // 只测纯函数会漏掉"/find 没把关键字传进来"——那正是原来命中处看不见的原因
        var src = System.IO.File.ReadAllText(Find());
        var calls = Occurrences(src, "FormatSearchHitLine(role, snippet, ConsoleColumns(), kw)");
        Assert.Equal(2, calls); // 会话日志与命名快照两条路径
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
