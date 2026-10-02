using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 第 298 轮把 <c>input</c> 归一化成「不含命中缓存」之后，**所有拿它当"输入总量"的消费方**
/// 都开始说错话。这条把那些消费方逐个钉住——修复语义时最容易漏掉的就是它们。
///
/// 四处，全部是同一个错误的变体：
///   <c>/stats</c> 的「输入 tokens」标签 —— 值对了，标签还写着"输入"
///   <c>/stats</c> 的「平均每次」 —— 加了 cached 才对得上账
///   状态栏的「X in」 —— 命中 99% 时与整个 prompt 差四个数量级
///   回合摘要的费用 —— 见 CostIncludesCachedTokensTests（已修）
/// </summary>
public sealed class NewInputTokenConsumersTests
{
    [Fact]
    public void TheStatsLabelSaysNewInputNotJustInput()
    {
        var src = ReadSource(FindProgram());
        // 「输入 tokens」会让人以为它是全部输入，缓存那一行再单独列出来就成了重复
        Assert.Contains("(\"新输入 tokens\"", src);
        Assert.DoesNotContain("(\"输入 tokens\"", src);
    }

    [Fact]
    public void TheAverageAddsCachedTokens()
    {
        // 修之前：新输入 42,954 + 输出 9 = 42,963，但「平均每次」也是 42,963——
        // 看起来对，其实缓存那部分（可能 41,590）从没进过这个数。
        var src = ReadSource(FindProgram());
        Assert.Contains(
            "var avgBase = agent.TotalInputTokens + agent.TotalOutputTokens + agent.TotalCachedTokens;",
            src);
    }

    [Fact]
    public void TheAverageNowReconcilesWithTheRowsAboveIt()
    {
        var program = ReadSource(FindProgram());
        // 把"平均"的算式提出来，和三行统计的口径逐字比一遍
        var avg = program
            .Split('\n')
            .First(l => l.Contains("var avgBase =", StringComparison.Ordinal));
        Assert.Contains("TotalInputTokens", avg);
        Assert.Contains("TotalOutputTokens", avg);
        Assert.Contains("TotalCachedTokens", avg);
    }

    [Fact]
    public void TheStatusBarShowsTheCacheRatioNextToIn()
    {
        // 只有「X in」时，命中 99% 缓存的那一轮 in≈1，而整个 prompt 有 4 万多——
        // 不说比例的话，这个数会被当成"上下文用了多少"。
        var bar = Program.BuildStatusBar(
            "code", "kilo/stealth/space-bunny-alpha", @"D:\Projects\CodeAgent", null,
            "1", "10", "ctx 4%", "think:high", 120, "99%");
        Assert.Contains("1 in / 10 out 99% cached", bar);
    }

    [Fact]
    public void WithoutACachedRatioTheBarIsUnchanged()
    {
        var bar = Program.BuildStatusBar(
            "code", "m", @"D:\Projects\CodeAgent", null,
            "1", "10", "ctx 4%", "think:high", 120);
        Assert.DoesNotContain("cached", bar);
        Assert.Contains("1 in / 10 out", bar);
    }

    [Fact]
    public void AnEmptyRatioAddsNothingToTheTokenSegment()
    {
        // 空命中率时 token 段必须与旧版**逐字相同**——多一个空格也会跟着复制出去。
        // （曾试图断言"out 后面没有空格"，但段分隔符本身就是 " · "，断言写不出来；
        //   与其写一条判不准的，不如直接比"与不传该参数时完全一致"。）
        foreach (var w in new[] { 40, 60, 80, 120 })
        {
            var withEmpty = Program.BuildStatusBar(
                "code", "m", @"D:\Projects\CodeAgent", "main", "12", "34", "ctx 4%", "high", w, "");
            var without = Program.BuildStatusBar(
                "code", "m", @"D:\Projects\CodeAgent", "main", "12", "34", "ctx 4%", "high", w);
            Assert.Equal(without, withEmpty);
        }
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void TheCachedNoteStillRespectsTheWidth(int width)
    {
        var bar = Program.BuildStatusBar(
            "code", "kilo/stealth/space-bunny-alpha", @"D:\Projects\CodeAgent", "main",
            "1", "10", "ctx 24%", "think:high", width, "99%");
        Assert.True(TextUtil.DisplayWidth(bar) <= width, $"宽度 {width} 超宽：{bar}");
    }

    [Fact]
    public void TheCachedNoteSurvivesWhileTheBarHasRoomAndIsDroppedWhenItDoesNot()
    {
        // 命中率和 in/out 说的是同一件事的两个面；放不下时整段 token 段先走（它是骨架），
        // 但 ctx 永远保留——占用率是不能丢的信息。
        var wide = Program.BuildStatusBar(
            "code", "m", @"D:\Projects\CodeAgent", "main",
            "1", "10", "ctx 24%", "high", 120, "99%");
        Assert.Contains("99% cached", wide);

        var tight = Program.BuildStatusBar(
            "code", "m", @"D:\Projects\CodeAgent", "main",
            "1", "10", "ctx 24%", "high", 40, "99%");
        Assert.DoesNotContain("cached", tight);
        Assert.Contains("ctx", tight);
    }

    [Fact]
    public void EveryConsumerUsesTheSameDenominator()
    {
        // in + cached 这个分母此前散在各处各算一遍。统一之后它们必须都长这样：
        // 状态栏与摘要行都直接写了这一串，不允许有人再写一份别的算法。
        var src = ReadSource(FindProgram());
        var occurrences = System.Text.RegularExpressions.Regex
            .Matches(src, @"PercentOf\((?:agent\.)?Turn(?:Cached|Input)Tokens")
            .Count + System.Text.RegularExpressions.Regex
            .Matches(src, @"PercentOf\(agent\.TotalCachedTokens").Count;
        Assert.True(occurrences >= 3, $"缓存比率散落在 {occurrences} 处，容易再分叉");
        Assert.DoesNotContain("PercentOf(agent.TurnCachedTokens, agent.TurnInputTokens)", src);
    }

    private static string ReadSource(string p) => File.ReadAllText(p).Replace("\r\n", "\n");

    private static string FindProgram()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(d, "src", "CodeAgent", "Program.cs");
                if (File.Exists(f))
                    return f;
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
