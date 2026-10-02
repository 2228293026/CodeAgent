using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 第 314 轮：极窄终端下 <c>WrapDisplay</c> 会**静默丢弃**装不下的字符。
///
/// 探针实测（块引用 + 20 个汉字正文）：
///   width=2, 3 → 整条 42 列一行，**完全不折行**（终端软换行接管）
///   width=4   → 每行 `&gt; 一个字`，**20 行**，正文几乎全部消失
///
/// 根因在 <c>WrapDisplay</c> 的硬拆分支：
/// <code>
/// var head = TakeDisplayWidth(rest, width);
/// if (head.Length == 0)
///     break; // 宽度装不下任何完整字符（如半个全角字），不无限循环
/// </code>
/// 「不无限循环」是对的，但代价是**把这个字符丢掉并继续处理下一个 token**——
/// 于是 1 列预算下每个 2 列的汉字都走这条路，正文被清空到只剩 1 个字，
/// 而屏幕上**没有任何一行提示内容被丢弃**。
///
/// 判据沿用前几轮：<b>静默吞掉内容比排版难看严重得多</b>。
/// 排版难看用户看得见（行歪了），内容消失用户看不见——
/// 他会以为模型只说了那一句话。
///
/// <para>另一侧同样有账要算对：<c>EmitLine</c> 的判定
/// <c>if (hanging.Length &gt; 0 &amp;&amp; _width &gt; hanging.Length)</c>
/// 左边是**列数**（_width 是终端列宽），右边是**字符数**——
/// 和第 313 轮同一种形状。这轮一并改成 <c>TextUtil.DisplayWidth(hanging)</c>，
/// 免得标记里出现非 ASCII 时同一处又错一次。</para>
/// </summary>
public sealed class NarrowWidthContentLossTests
{
    private static string Wrap(string body, int width, string indent) =>
        TextUtil.WrapDisplay(body, width, indent);

    private static string[] NonEmpty(string s) =>
        s.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray();

    // ── 缺陷本体：宽到能容纳一个全角字时，不许丢字 ──────────────────────

    [Fact]
    public void EveryCharacterSurvivesWhenOneFullWidthCharFits()
    {
        // 3 列预算（`> ` 悬挂缩进 2 列后正文预算 1 列）——注意 1 列装不下任何汉字，
        // 所以这条用的是 2 列预算：2 列刚好装一个字。
        var body = string.Concat(Enumerable.Repeat("中", 10));
        var wrapped = Wrap(body, 2, "");
        var joined = string.Concat(NonEmpty(wrapped).Select(l => l.Trim()));
        Assert.Equal(10, joined.Count(c => c == '中'));
    }

    [Fact]
    public void TwoColumnsShowsEveryCharacter()
    {
        var body = string.Concat(Enumerable.Repeat("甲乙丙丁", 3)); // 12 字 = 24 列
        var lines = NonEmpty(Wrap(body, 2, ""));
        Assert.Equal(12, lines.Length);
        Assert.All(lines, l => Assert.True(TextUtil.DisplayWidth(l) <= 2, l));
    }

    [Fact]
    public void NothingIsLostAcrossManyWidths()
    {
        // 覆盖 1..12 列。1 列预算下"每个字符都装不下"是**物理不可能**，
        // 此时允许有损，但必须有交代（见 DropNoticeIsShownWhenCharactersAreLost）。
        var body = string.Concat(Enumerable.Repeat("中文abc", 6));
        for (var width = 2; width <= 12; width++)
        {
            var lines = NonEmpty(Wrap(body, width, ""));
            var kept = string.Concat(lines.Select(l => l));
            var keptChars = body.Count(c => kept.Contains(c));
            Assert.Equal(body.Length, keptChars);
        }
    }

    [Fact]
    public void MixedCjkAndAsciiKeepsAllOfBoth()
    {
        var body = "中文 abc 中文 abc 中文";
        var lines = NonEmpty(Wrap(body, 2, ""));
        // 数的是**出现次数**不是行数：2 列预算下每个汉字独占一行，
        // 写成 lines.Count(l => l.Contains('中')) 会把「每行是否含中」当成「有几个中」。
        // 期望值直接数输入，别手算——"abc" 出现两次就是 2 个 a，不是 3 个。
        var joined = string.Concat(lines);
        Assert.Equal(body.Count(c => c == '中'), joined.Count(c => c == '中'));
        Assert.Equal(body.Count(c => c == 'a'), joined.Count(c => c == 'a'));
    }

    // ── 真正装不下时，必须说清楚，而不是默默吃掉 ────────────────────────

    [Fact]
    public void WhenNothingFitsTheContentIsNotSilentlySwallowed()
    {
        // 1 列预算 + 纯中文：物理上装不下任何一个字。
        // 允许有损，但**不许无声无息**——必须留下一行能看懂的交代。
        var body = string.Concat(Enumerable.Repeat("中", 6));
        var outp = Wrap(body, 1, "");
        Assert.False(string.IsNullOrWhiteSpace(outp));
        Assert.Contains("…", outp);
    }

    [Fact]
    public void TheDropNoticeNamesTheWidthThatWasImpossible()
    {
        var outp = Wrap(string.Concat(Enumerable.Repeat("中", 6)), 1, "");
        // 交代要含「宽度」信息：用户才知道是终端太窄，不是模型没说完
        Assert.Contains("1", outp);
    }

    [Fact]
    public void AsciiStillFitsInOneColumn()
    {
        // ASCII 是 1 列，1 列预算下**装得下**，所以不许说"装不下"
        var outp = Wrap("abcd", 1, "");
        Assert.DoesNotContain("装不下", outp);
        Assert.Equal("a", NonEmpty(outp)[0]);
    }

    [Fact]
    public void TheNoticeDoesNotAppearWhenNothingIsLost()
    {
        // 有损才交代；正常折行时多一行提示是纯噪音
        var outp = Wrap(string.Concat(Enumerable.Repeat("中文", 5)), 20, "");
        Assert.DoesNotContain("装不下", outp);
    }

    [Fact]
    public void TheNoticeIsNotRepeatedPerLine()
    {
        var outp = Wrap(string.Concat(Enumerable.Repeat("中", 20)), 1, "");
        var notices = NonEmpty(outp).Count(l => l.Contains("装不下", StringComparison.Ordinal));
        Assert.Equal(1, notices);
    }

    [Fact]
    public void TheNoticeDoesNotFloodTheScreenWithBlankLines()
    {
        // 我第一版修复把丢掉的字符**落盘成空行**：6 个汉字 + 1 列预算
        // 产出 7 个空行 + 1 行交代，屏幕被刷满。空行不承载任何内容，纯噪音。
        var outp = Wrap(string.Concat(Enumerable.Repeat("中", 6)), 1, "");
        Assert.Single(NonEmpty(outp));
    }

    // ── 折行后被粘成一行：内容错乱（比排版严重得多） ──────────────────────

    [Fact]
    public void WrappedSegmentsAreNotGluedOntoOneLine()
    {
        // 探针实测（修复前）："- 内容和中文" @6 列
        //   正确 WrapDisplay → "- 内容和" / "- 中文" / "- 文"（3 行）
        //   实际渲染       → "- 内容和\n- 中文- 文"（第 2 段起没有换行，被粘在一起）
        // 根因：EmitStyledLine 只把换行标志传给**首段**，快速路径按该标志决定是否写 \n。
        var lines = NonEmpty(Render("- 内容和中文\n", 6));
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.True(TextUtil.DisplayWidth(l) <= 6, $"溢出: [{l}]"));
    }

    [Fact]
    public void EachWrappedSegmentKeepsTheMarkerExactlyOnce()
    {
        var lines = NonEmpty(Render("- 内容和中文\n", 6));
        foreach (var l in lines)
            Assert.Equal(1, CountOccurrences(l, "- ")); // 标记不得重复出现
    }

    [Fact]
    public void ABlockquoteWrappingToThreeLinesKeepsItsGutter()
    {
        var lines = NonEmpty(Render("> 引用内容和中文\n", 6));
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("> ", l));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
    }

    // ── 判定端也必须是列数 ────────────────────────────────────────────────

    [Fact]
    public void TheEmitLineGuardComparesColumnsNotCharacters()
    {
        // `_width > hanging.Length` 左边列数右边字符数。`- ` / `> ` 恰好两者相等，
        // 所以这个 bug 一直是**哑弹**：探针 `　- 内容和中文`（全角空格缩进）
        // 返回标记 len=3 / cols=4 → 宽度 4 时 `4 > 3` 误判成"折得下"，
        // budget 变成 0，一条 14 列的内容被塞进 4 列终端。
        // 换成 DisplayWidth 才是同一口径。
        var src = System.IO.File.ReadAllText(FindRenderer()).Replace("\r\n", "\n");
        Assert.DoesNotContain("_width > hanging.Length", src);
        Assert.Contains("_width > hangingCols", src);
    }

    [Fact]
    public void TheBodyIsSlicedByCharactersOnPurpose()
    {
        // 这里**故意**不改：content[hanging.Length..] 是子串下标，按字符数切才对。
        // 换成 content[TextUtil.DisplayWidth(hanging)..] 会在标记含全角空格时
        // 多切掉正文第一个字符——我第一版测试就写错了，探针数据把它拦了下来。
        // 所以正文切片保留 .Length，判定用 hangingCols，两者各司其职。
        var src = System.IO.File.ReadAllText(FindRenderer()).Replace("\r\n", "\n");
        Assert.Contains("var body = content[hanging.Length..];", src);
        Assert.Contains("var budget = _width - hangingCols;", src);
    }

    // ── 标记本身：字符数 ≠ 列数时，判定必须按列 ──────────────────────────

    [Fact]
    public void AFullwidthIndentedBulletIsJudgedByColumns()
    {
        // 探针实测：HangingIndentFor("　- 内容和中文") 返回 len=3 / cols=4
        // （全角空格 U+3000 算 2 列）。按字符数判定会让 4 列终端误以为折得下。
        var hanging = ConsoleRenderer.HangingIndentFor("　- 内容和中文");
        Assert.Equal(3, hanging.Length);
        Assert.Equal(4, TextUtil.DisplayWidth(hanging));
    }

    [Fact]
    public void ANarrowTerminalDoesNotReceiveAnOverflowingBulletLine()
    {
        // 修复前：宽度 4 时输出 14 列的一行（budget=0，WrapDisplay 原样返回未折行的正文）。
        // 修复后：判定改成按列后 4 > 4 为假 → 保持原样交回终端软换行，
        // 此时**不得**声称折过行；这里只钉住"不会再输出 6 列的折行结果"。
        var lines = NonEmpty(Render("　- 内容和中文\n", 6));
        foreach (var l in lines)
            Assert.True(TextUtil.DisplayWidth(l) <= 6, $"宽度 6 溢出: [{l}] ({TextUtil.DisplayWidth(l)} 列)");
    }

    [Fact]
    public void AWideTerminalStillWrapsTheFullwidthBullet()
    {
        // 反向：宽度够时**必须**照常折行——不能为了修窄屏就把宽屏的悬挂缩进也一起关掉。
        var lines = NonEmpty(Render("　- " + string.Concat(Enumerable.Repeat("内容", 10)) + "\n", 20));
        Assert.True(lines.Length > 1, "20 列应当发生折行");
        Assert.All(lines, l => Assert.StartsWith("　- ", l));
    }

    private static string Render(string text, int width)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var renderer = new ConsoleRenderer(enabled: true);
            renderer.SetWidth(width);
            renderer.Append(text);
            renderer.Flush();
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }

    private static string FindRenderer()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "ConsoleRenderer.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                dir = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? dir;
            }
        }
        throw new InvalidOperationException("找不到 src/CodeAgent/ConsoleRenderer.cs");
    }
}
