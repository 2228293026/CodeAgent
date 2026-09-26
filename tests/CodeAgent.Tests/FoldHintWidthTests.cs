using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 多行输入折叠提示行的宽度安全。
/// 回归点：末行预览此前写死 40 列，窄终端上提示行（≈28 列前缀 + 40）会**折行**，
/// 而折叠块依赖固定 3 行高（菜单锚点/光标定位都按这个算）——折行会让整块变 4 行，
/// 菜单随即画在错误的位置。
/// </summary>
public class FoldHintWidthTests
{
    private static string LongTail() => new string('x', 200);

    [Fact]
    public void FoldTailBudget_UnknownWidthKeepsTheFixedBudget()
    {
        Assert.Equal(InputLine.FoldTailPreviewWidth, InputLine.FoldTailBudget(0, 10));
        Assert.Equal(InputLine.FoldTailPreviewWidth, InputLine.FoldTailBudget(-1, 10));
    }

    [Fact]
    public void FoldTailBudget_NeverExceedsTheFixedBudget()
    {
        Assert.Equal(InputLine.FoldTailPreviewWidth, InputLine.FoldTailBudget(200, 10));
    }

    [Fact]
    public void FoldTailBudget_ShrinksOnNarrowTerminals()
    {
        var budget = InputLine.FoldTailBudget(50, 10);
        Assert.True(budget > 0 && budget < InputLine.FoldTailPreviewWidth, $"实际 {budget}");
    }

    [Fact]
    public void FoldTailBudget_ReturnsZeroWhenNothingUsefulFits()
    {
        Assert.Equal(0, InputLine.FoldTailBudget(20, 10));
    }

    [Fact]
    public void FoldHintPrefixWidthCountsDisplayColumns()
    {
        // 提示几乎全是中文：按字符数算会低估近一倍
        var width = InputLine.FoldHintPrefixWidth(10);
        Assert.True(width > 10, $"全中文前缀的显示宽度应大于字符数，实际 {width}");
    }

    [Fact]
    public void FoldedHintLineNeverExceedsTheWindow()
    {
        foreach (var width in new[] { 30, 40, 50, 60, 80, 120, 200 })
        {
            var folded = InputLine.FoldText("第一行\n第二行\n第三行\n第四行\n" + LongTail(), InputLine.FoldThreshold, width);
            var hint = folded.Split('\n')[^1];
            Assert.True(TextUtil.DisplayWidth(hint) <= width,
                $"窗口 {width} 列时提示行 {TextUtil.DisplayWidth(hint)} 列：{hint}");
        }
    }

    [Fact]
    public void FoldBlockKeepsItsFixedHeightOnNarrowTerminals()
    {
        // 折叠块必须是「前 threshold-1 行 + 1 行提示」= 3 行，否则菜单锚点错位
        var folded = InputLine.FoldText("a\nb\nc\nd\ne\nf", InputLine.FoldThreshold, 40);
        Assert.Equal(InputLine.FoldThreshold, DiffUtil.CountLines(folded));
    }

    [Fact]
    public void VeryNarrowTerminalDropsTheTailEntirely()
    {
        var folded = InputLine.FoldText("a\nb\nc\n" + LongTail(), InputLine.FoldThreshold, 20);
        var hint = folded.Split('\n')[^1];
        Assert.DoesNotContain("末行", hint);
        Assert.Contains("展开", hint);
    }

    [Fact]
    public void WideTerminalKeepsTheTailPreview()
    {
        var folded = InputLine.FoldText("a\nb\nc\n末行内容在这里", InputLine.FoldThreshold, 200);
        Assert.Contains("末行: 末行内容在这里", folded);
    }

    [Fact]
    public void ShortInputIsNotFoldedAtAll()
    {
        var text = "只有一行";
        Assert.Equal(text, InputLine.FoldText(text, InputLine.FoldThreshold, 40));
    }

    [Fact]
    public void TailIsStillBoundedByTheFixedMaximum()
    {
        var folded = InputLine.FoldText("a\nb\nc\n" + LongTail(), InputLine.FoldThreshold, 200);
        var tail = folded.Split('\n')[^1].Split("末行: ")[1];
        Assert.True(TextUtil.DisplayWidth(tail) <= InputLine.FoldTailPreviewWidth);
    }

    [Fact]
    public void MinFoldTailWidthIsUsable()
    {
        Assert.True(InputLine.MinFoldTailWidth >= 4, "少于 4 列的末行预览没有信息量");
    }
}
