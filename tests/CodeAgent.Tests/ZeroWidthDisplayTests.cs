using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 零宽字符的显示宽度。
/// 回归点：此前每个字符只要 &gt; U+2E7F 就按 2 列算，**组合附加符号、ZWJ、
/// 变体选择符全被算成 1 列**——它们在终端上根本不占列。结果：
///   · 「👨‍👩‍👧」算成 6 列，实际 2 列
///   · 「é」（e + U+0301 组合尖音符）算成 2 列，实际 1 列
/// 表格列宽、状态栏、提示符都按多算出来的宽度补空格 → 越排越歪，最终溢出行宽。
/// </summary>
public class ZeroWidthDisplayTests
{
    [Fact]
    public void CombiningMarkIsZeroWidth()
    {
        // e + 组合尖音符 = 终端里 1 列
        Assert.Equal(1, TextUtil.DisplayWidth("é"));
    }

    [Fact]
    public void ZwjEmojiSequenceIsTwoColumns()
    {
        // 👨‍👩‍👧 三个 emoji 由两个 ZWJ 连接，终端整体只占 2 列
        Assert.Equal(2, TextUtil.DisplayWidth("👨‍👩‍👧"));
    }

    [Fact]
    public void WomanTechnologistIsTwoColumns()
    {
        Assert.Equal(2, TextUtil.DisplayWidth("👩‍💻"));
    }

    [Fact]
    public void VariationSelectorIsZeroWidth()
    {
        // U+FE0F 让基础 emoji 变成彩色，但它自己不占列：
        // 加不加它，显示宽度必须**完全相同**。
        Assert.Equal(TextUtil.DisplayWidth("❤"), TextUtil.DisplayWidth("❤️"));
        Assert.Equal(1, TextUtil.DisplayWidth("❤️"));
    }

    [Fact]
    public void PlainEmojiIsUnchanged()
    {
        Assert.Equal(2, TextUtil.DisplayWidth("😀"));
    }

    [Fact]
    public void MixedTextMeasuresCorrectly()
    {
        Assert.Equal(4 + 2, TextUtil.DisplayWidth("abcd😀"));
        Assert.Equal(4, TextUtil.DisplayWidth("中文"));
    }

    [Fact]
    public void ZeroWidthHelperIsExposedForChecks()
    {
        Assert.True(TextUtil.IsZeroWidth('́'));
        Assert.True(TextUtil.IsZeroWidth('‍'));
        Assert.True(TextUtil.IsZeroWidth('️'));
        Assert.False(TextUtil.IsZeroWidth('中'));
        Assert.False(TextUtil.IsZeroWidth('a'));
    }

    [Fact]
    public void LoneSurrogateStillCountsAsOneColumn()
    {
        // 孤立代理按 1 列（终端渲染为单宽替换符），不能因本次改动变成 0
        Assert.Equal(1, TextUtil.DisplayWidth("\uD83E"));
    }

    [Fact]
    public void StatusBarWithZwjEmojiFits()
    {
        // 状态栏按显示宽度补齐：📊(2) + 空格(1) + 完成(4) + 空格(1) + 👨‍👩‍👧(2)
        var bar = "📊 完成 👨‍👩‍👧";
        Assert.Equal(10, TextUtil.DisplayWidth(bar));
    }

    [Fact]
    public void TableRowWithEmojiStaysAligned()
    {
        // 单元格内容含 ZWJ 序列时，表格补空格必须按真实列宽
        var cell = "👨‍👩‍👧";
        var pad = 10 - TextUtil.DisplayWidth(cell);
        Assert.Equal(8, pad);
    }

    [Fact]
    public void NeverOverestimatesPlainAscii()
    {
        const string ascii = "read_file agent/report.md";
        Assert.Equal(ascii.Length, TextUtil.DisplayWidth(ascii));
    }
}
