using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 反向搜索（Ctrl+R）输入行的宽度安全。
/// 回归点：查询串此前写死 24 列。窄终端上「提示符 + (搜索) + 24 列查询 + 标记」
/// 就能把整行占满，草稿被完全挤出屏幕——用户按 Ctrl+R 反而看不到自己在搜什么。
/// </summary>
public class SearchRowWidthTests
{
    private const string Prompt = "› ";

    [Fact]
    public void SearchQueryBudget_UnknownWidthKeepsTheFixedBudget()
    {
        Assert.Equal(InputLine.SearchQueryDisplayWidth, InputLine.SearchQueryBudget(0, 2));
        Assert.Equal(InputLine.SearchQueryDisplayWidth, InputLine.SearchQueryBudget(-1, 2));
    }

    [Fact]
    public void SearchQueryBudget_NeverExceedsTheFixedBudget()
    {
        Assert.Equal(InputLine.SearchQueryDisplayWidth, InputLine.SearchQueryBudget(400, 2));
    }

    [Fact]
    public void SearchQueryBudget_ShrinksOnNarrowTerminals()
    {
        // 30 列：提示符 2 + 固定开销 10 后只剩 18 列给查询串
        var budget = InputLine.SearchQueryBudget(30, TextUtil.DisplayWidth(Prompt));
        Assert.True(budget > 0 && budget < InputLine.SearchQueryDisplayWidth, $"实际 {budget}");
    }

    [Fact]
    public void SearchQueryBudget_JustWideEnoughKeepsTheFullBudget()
    {
        // 恰好放得下（2 + 10 + 24 = 36）时不应裁剪
        Assert.Equal(InputLine.SearchQueryDisplayWidth,
            InputLine.SearchQueryBudget(36, TextUtil.DisplayWidth(Prompt)));
    }

    [Fact]
    public void SearchQueryBudget_ReturnsZeroWhenNothingUsefulFits()
    {
        Assert.Equal(0, InputLine.SearchQueryBudget(10, TextUtil.DisplayWidth(Prompt)));
    }

    [Fact]
    public void SearchRowKeepsTheDraftVisibleOnNarrowTerminals()
    {
        var draft = "这是一段草稿内容";
        var line = InputLine.FormatInputText(Prompt, true, "很长的查询字符串", true, 0, 0, draft,
            InputLine.SearchQueryDisplayWidth, windowWidth: 40);
        // 关键保证：草稿必须在（整行不会被搜索前缀顶出屏幕）
        Assert.Contains(draft, line);
    }

    [Fact]
    public void VeryNarrowTerminalDropsTheQueryButKeepsTheStatus()
    {
        var draft = "草稿";
        var line = InputLine.FormatInputText(Prompt, true, "查询", true, 0, 0, draft,
            InputLine.SearchQueryDisplayWidth, windowWidth: 10);
        // 与其显示一两个残缺字符，不如保留命中状态与草稿
        Assert.DoesNotContain("`", line);
        Assert.Contains("草稿", line);
        Assert.Contains("✔", line);
    }

    [Fact]
    public void VeryNarrowTerminalStillReportsMiss()
    {
        var line = InputLine.FormatInputText(Prompt, true, "查询", false, 0, 0, "草稿",
            InputLine.SearchQueryDisplayWidth, windowWidth: 10);
        Assert.Contains("未命中", line);
    }

    [Fact]
    public void EmptyQueryShowsNoQueryMarkup()
    {
        var line = InputLine.FormatInputText(Prompt, true, "", true, 0, 0, "草稿",
            InputLine.SearchQueryDisplayWidth, windowWidth: 80);
        Assert.DoesNotContain("`", line);
        Assert.Contains("草稿", line);
    }

    [Fact]
    public void WideTerminalShowsTheFullQuery()
    {
        var line = InputLine.FormatInputText(Prompt, true, "查一下", true, 0, 0, "",
            InputLine.SearchQueryDisplayWidth, windowWidth: 200);
        Assert.Contains("`查一下`", line);
        Assert.Contains("✔", line);
    }

    [Fact]
    public void MissIsDistinctFromHitEvenWhenNarrow()
    {
        var hit = InputLine.FormatInputText(Prompt, true, "查询", true, 0, 0, "",
            InputLine.SearchQueryDisplayWidth, windowWidth: 10);
        var miss = InputLine.FormatInputText(Prompt, true, "查询", false, 0, 0, "",
            InputLine.SearchQueryDisplayWidth, windowWidth: 10);
        Assert.NotEqual(hit, miss);
    }

    [Fact]
    public void UnknownWidthKeepsThePreviousAppearance()
    {
        // 宽度未知时与旧行为逐字一致：观测不因本次改动而变化
        var line = InputLine.FormatInputText(Prompt, true, "查一下", true, 0, 0, "草稿");
        Assert.Equal($"{Prompt} (搜索)`查一下` ✔ 草稿", line);
    }

    [Fact]
    public void HistoryModeIsUnaffected()
    {
        var line = InputLine.FormatInputText(Prompt, false, "", false, 2, 7, "草稿",
            InputLine.SearchQueryDisplayWidth, windowWidth: 40);
        Assert.Contains("(历史 3/7)", line);
        Assert.Contains("草稿", line);
    }

    [Fact]
    public void PlainModeIsUnaffected()
    {
        Assert.Equal(Prompt + "草稿",
            InputLine.FormatInputText(Prompt, false, "", false, 0, 0, "草稿",
                InputLine.SearchQueryDisplayWidth, windowWidth: 40));
    }
}
