using System;
using System.IO;
using System.Text.RegularExpressions;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 底部状态栏（Claude Code 风格的 <c>main | dir | model | ↑in ↓out | ctx% | HH:MM</c>）。
///
/// 与顶部 <c>BuildStatusBar</c> 的分工：顶部是**本轮**视角，底部是**会话**视角。
/// 两行结构相似、分隔符却必须不同——人眼会把分隔符一致的两行当成同一行读。
/// </summary>
[Collection("ConsoleOutput")]
public class FooterBarTests
{
    private const string Sep = "|";

    [Fact]
    public void WideBarCarriesEverySegment()
    {
        var bar = Program.BuildFooterBar("main", "D:/Projects/CodeAgent", "space-bunny-alpha",
            37873, 132, "ctx 3%", "21:14", 120);
        Assert.Equal($"main{Sep}D:/Projects/CodeAgent{Sep}space-bunny-alpha{Sep}↑37.9k ↓132{Sep}ctx 3%{Sep}21:14", bar);
        Assert.True(TextUtil.DisplayWidth(bar) <= 120);
    }

    [Fact]
    public void MissingBranchIsOmittedNotEmptied()
    {
        var bar = Program.BuildFooterBar(null, "D:/P", "m", 1, 2, "ctx 1%", "21:14", 120);
        Assert.DoesNotContain($"{Sep}{Sep}", bar);
        Assert.StartsWith("D:/P", bar);
        var none = Program.BuildFooterBar("", "D:/P", "m", 1, 2, "ctx 1%", "21:14", 120);
        Assert.Equal(bar, none);
    }

    [Fact]
    public void NarrowDropsTheClockFirst()
    {
        // 时钟每秒都在变，少一列无所谓；上下文占用不能少
        var bar = Program.BuildFooterBar("main", "D:/Projects/CodeAgent", "space-bunny-alpha",
            37873, 132, "ctx 3%", "21:14", 50);
        Assert.DoesNotContain("21:14", bar);
        Assert.Contains("ctx 3%", bar);
        Assert.True(TextUtil.DisplayWidth(bar) <= 50, $"超宽: {TextUtil.DisplayWidth(bar)}");
    }

    [Fact]
    public void NarrowerStillKeepsTokensAndContextWhenTheyFit()
    {
        // 越窄越往右退；宽度还够时 token 与 ctx 都要在
        for (var w = 18; w <= 60; w++)
        {
            var bar = Program.BuildFooterBar("main", "D:/Projects/CodeAgent", "space-bunny-alpha",
                37873, 132, "ctx 3%", "21:14", w);
            Assert.Contains("ctx 3%", bar);
            Assert.True(TextUtil.DisplayWidth(bar) <= w, $"宽度 {w} 溢出: {TextUtil.DisplayWidth(bar)} :: {bar}");
        }
    }

    [Fact]
    public void NeverOverEmptiesEvenAtAbsurdWidths()
    {
        foreach (var w in new[] { 1, 3, 5, 8, 11, 17 })
        {
            var bar = Program.BuildFooterBar("main", "D:/Projects/CodeAgent", "model",
                37873, 132, "ctx 3%", "21:14", w);
            Assert.False(string.IsNullOrEmpty(bar), $"宽度 {w} 输出为空");
            Assert.True(TextUtil.DisplayWidth(bar) <= w, $"宽度 {w} 溢出: {TextUtil.DisplayWidth(bar)}");
        }
    }

    [Fact]
    public void ContextSegmentIsAPercentageNotATriple()
    {
        // 顶部状态栏用 `37.9k/1.0M (3%)`（三段，最有用的百分比被挤进括号）；
        // 底部只给百分比——"还能聊多久"才是要一眼看到的。
        var known = Program.FooterCtxText(50_000, 1_000_000);
        var unknown = Program.FooterCtxText(50_000, 0);
        var zero = Program.FooterCtxText(0, 1_000_000);
        Assert.StartsWith("ctx ", known);
        Assert.EndsWith("%", known);
        Assert.DoesNotContain("/", known);
        Assert.DoesNotContain("(", known);
        // 窗口未知时只给已用量，且不能谎称 0%
        Assert.StartsWith("ctx ", unknown);
        Assert.DoesNotContain("%", unknown);
        // 已用量为 0 时百分比没有意义，给 0 会让人以为"没占"
        Assert.DoesNotContain("%", zero);
        Assert.StartsWith("ctx ", zero);
    }

    [Fact]
    public void UnknownWidthReturnsEverything()
    {
        // 宽度未知时不做任何猜测，宁可完整也不截错
        var bar = Program.BuildFooterBar("main", "D:/Projects/CodeAgent", "m", 1, 2, "ctx 1%", "21:14", 0);
        Assert.Contains("ctx 1%", bar);
        Assert.Contains("21:14", bar);
    }

    [Fact]
    public void FooterUsesAPipeWhileTheTopBarUsesAMiddot()
    {
        // 关键回归：两行结构相似，分隔符必须不同，否则会被读成同一行
        Assert.Equal("|", SafeColor.Glyphs.FooterSep);
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = _ => null;
            var top = Program.BuildStatusBar("code", "m", "D:/P", "main", "0", "0", "ctx 1%", "think:high", 120);
            var bottom = Program.BuildFooterBar("main", "D:/P", "m", 0, 0, "ctx 1%", "21:14", 120);
            Assert.Contains(SafeColor.Glyphs.SegmentSeparator, top);
            Assert.DoesNotContain(SafeColor.Glyphs.SegmentSeparator, bottom);
        }
        finally { SafeColor.ReadEnv = saved; }
    }

    [Fact]
    public void TheTurnSummaryActuallyPrintsTheFooter()
    {
        var source = ReadSource();
        Assert.Contains("BuildFooterBar(", source);
        // 源码守卫：只测 builder 会漏掉「没接进每轮结束」这一步
        Assert.Matches(@"(?s)PrintTurnSummary.*BuildFooterBar\(", source);
    }

    private static string ReadSource()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return File.ReadAllText(candidate);
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
