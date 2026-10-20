using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 提示行里的标记只能出现**一次**。
///
/// 修的是一个看起来很小、实际很伤的缺陷：<c>FormatBareBangHint</c> 与
/// <c>FormatBareAtHint</c> 的 body 里手写了 ⚠，而 <see cref="Program.FormatNoticeLine"/>
/// 又会加一个，于是行首变成「⚠ ⚠ 叹号后面…」。
///
/// 用户看到双标记的第一反应是「这程序是不是坏了」，
/// 而不是去读提示内容——本该解释问题的一行，反而制造了新问题。
///
/// 顺带修了另一半：宽度为 0 时手写的 else 分支直接返回 body，
/// 把标记整个漏掉了。宽度未知时也不该丢标记。
/// </summary>
public class NoticeMarkerTests
{
    private static int Count(string text, string needle) =>
        text.Split(needle).Length - 1;

    [Fact]
    public void BangHintHasExactlyOneMarker()
    {
        var text = Program.FormatBareBangHint(80);
        Assert.Equal(1, Count(text, SafeColor.Glyphs.Warn));
        Assert.Contains("叹号后面", text);
    }

    [Fact]
    public void AtHintHasExactlyOneMarker()
    {
        var text = Program.FormatBareAtHint(80);
        Assert.Equal(1, Count(text, SafeColor.Glyphs.Warn));
        Assert.Contains("@ 后面", text);
    }

    [Fact]
    public void MarkerSurvivesUnknownWidth()
    {
        // 宽度未知（0）不裁剪，但**标记不能丢**：
        // 此前这里手写 else 直接返回 body，于是宽 0 时行首光秃秃的
        Assert.Equal(1, Count(Program.FormatBareBangHint(0), SafeColor.Glyphs.Warn));
        Assert.Equal(1, Count(Program.FormatBareAtHint(0), SafeColor.Glyphs.Warn));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void MarkerStaysOnTheFirstColumn(int width)
    {
        // 标记必须在**行首**：截断整行会让 ⚠ 孤零零留在上一行，
        // 扫读时反而找不到告警（FormatNoticeLine 的文档写的就是这条）
        foreach (var text in new[] { Program.FormatBareBangHint(width), Program.FormatBareAtHint(width) })
        {
            Assert.StartsWith(SafeColor.Glyphs.Warn, text);
            Assert.True(TextUtil.DisplayWidth(text) <= width,
                $"width={width} 超宽（{TextUtil.DisplayWidth(text)} 列）：{text}");
        }
    }

    [Fact]
    public void InvalidValueHintIsAlsoSingleMarked()
    {
        // 同一族提示（/shell basj 这类）也走 FormatNoticeLine，同样只许一个标记
        var text = Program.FormatInvalidValueLine("/shell", "basj", new[] { "cmd", "bash" }, 80);
        Assert.Equal(1, Count(text, SafeColor.Glyphs.Warn));
    }

    [Fact]
    public void SuggestionsAreSingleMarked()
    {
        var lines = Program.FormatInvalidValueLines("/shell", "basj", new[] { "bash", "cmd" }, 80);
        Assert.True(lines.Count >= 1, string.Join(" | ", lines));
        Assert.All(lines, line => Assert.Equal(1, Count(line, SafeColor.Glyphs.Warn)));
    }
}
