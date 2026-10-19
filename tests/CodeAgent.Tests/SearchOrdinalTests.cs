using System;
using System.Collections.Generic;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>Ctrl+R</c> 搜索的「第 n/M 条」计数。
///
/// 此前只标一个「命中」：看不出**有多个匹配**，也看不出**还能往前翻多少**。
/// 这正是 Ctrl+R 最常被误解的地方——用户再按一次没反应，其实只是到头了，
/// 而界面看上去和「卡住了」一模一样。
///
/// 序号按**从最新往回数**：第一次按 Ctrl+R 落在最新那条，那时显示 1/M 才直观。
/// </summary>
public class SearchOrdinalTests
{
    private static List<string> Hist => new()
    {
        "fix the parser",
        "add tests for parser",
        "unrelated thing",
        "parser again",
    };

    [Fact]
    public void CountsEveryMatch()
    {
        Assert.Equal(3, InputLine.CountHistoryMatches(Hist, "parser"));
    }

    [Fact]
    public void CountIsCaseInsensitive()
    {
        // 与 FindHistoryMatch 同一口径：大小写不敏感
        Assert.Equal(3, InputLine.CountHistoryMatches(Hist, "PARSER"));
    }

    [Fact]
    public void EmptyQueryHasNoMatches()
    {
        Assert.Equal(0, InputLine.CountHistoryMatches(Hist, ""));
        Assert.Equal(0, InputLine.CountHistoryMatches([], "x"));
    }

    [Fact]
    public void TheFirstMatchIsOrdinalOne()
    {
        // 最新那条命中是「第 1 条」：Ctrl+R 从最新往回翻
        Assert.Equal(1, InputLine.HistoryMatchOrdinal(Hist, "parser", 3));
    }

    [Fact]
    public void OlderMatchesCountUp()
    {
        // index 1 是 "add tests for parser"，从最新数过来正好第 2 条
        Assert.Equal(2, InputLine.HistoryMatchOrdinal(Hist, "parser", 1));
        // index 0 是 "fix the parser"，第 3 条（也是最后一条）
        Assert.Equal(3, InputLine.HistoryMatchOrdinal(Hist, "parser", 0));
    }

    [Fact]
    public void NonMatchingIndexIsNotAnOrdinal()
    {
        // index 2 是 "unrelated thing"，自己不算一条命中——从最新往回数只数到 index 3 那条，
        // 所以是 1。实际路径不会走到这里（searchFrom 永远指向命中项），
        // 但函数必须给出一个确定的答案而不是另一个数。
        Assert.Equal(1, InputLine.HistoryMatchOrdinal(Hist, "parser", 2));
        // 越界 / 空查询没有序号可言
        Assert.Equal(0, InputLine.HistoryMatchOrdinal(Hist, "parser", -1));
        Assert.Equal(0, InputLine.HistoryMatchOrdinal(Hist, "", 0));
    }

    [Fact]
    public void TheOrdinalShowsUpInTheSearchLine()
    {
        var text = InputLine.FormatInputText(
            "D:\\P> ", searching: true, searchQuery: "parser", searchHit: true,
            historyIndex: 0, historyCount: 4, draft: "fix the parser",
            windowWidth: 120, colored: false, searchOrdinal: 3, searchTotal: 3);
        Assert.Contains("3/3", text);
    }

    [Fact]
    public void NoOrdinalWithoutMatches()
    {
        var text = InputLine.FormatInputText(
            "D:\\P> ", searching: true, searchQuery: "zzz", searchHit: false,
            historyIndex: -1, historyCount: 4, draft: "",
            windowWidth: 120, colored: false);
        Assert.Contains("未命中", text);
        Assert.DoesNotContain("/", text.Replace("D:/", ""));
    }

    [Fact]
    public void NarrowWidthStillNeverExceedsTheBudget()
    {
        // 多出来的「第 n/M 条」不能把搜索行顶到折行
        for (var w = 20; w <= 100; w++)
        {
            var text = InputLine.FormatInputText(
                "D:\\P> ", searching: true, searchQuery: "a very long query string indeed", searchHit: true,
                historyIndex: 0, historyCount: 9, draft: "draft text here",
                windowWidth: w, colored: false, searchOrdinal: 7, searchTotal: 9);
            var line = text.Split('\n')[0];
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"宽度 {w}：搜索行 {TextUtil.DisplayWidth(line)} 列越界 → [{line}]");
        }
    }
}
