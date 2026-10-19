using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 长输入行的光标可见性。
///
/// 输入行超宽时目前是 <c>prefix + FitToWidth(draft, avail)</c>——**保住头部、切掉尾部**。
/// 而正常打字时光标就在尾部：于是敲过列宽之后，用户正在打的那个字
/// 一进去就被切掉了。屏幕上看不出任何变化，像键盘失灵。
///
/// 输入行必须像终端的 shell 一样**横向滚动**：只保证光标附近可见。
/// </summary>
public class LongInputVisibilityTests
{
    [Fact]
    public void TypingAtTheTailStaysVisible()
    {
        // 40 列窗口，打 80 个字符：最后一个字符必须出现在可见文本里
        var draft = new string('a', 60) + "TAIL";
        var text = InputLine.DraftTail("> ", draft, 40);
        Assert.True(TextUtil.DisplayWidth(text) <= 40, $"越界：{TextUtil.DisplayWidth(text)} 列");
        Assert.Contains("TAIL", text);
    }

    [Fact]
    public void HeadIsDroppedNotTheTail()
    {
        // 用户已经打完的部分可以滚出去，正在打的不能
        var draft = new string('x', 100) + "最新";
        var text = InputLine.DraftTail("> ", draft, 30);
        Assert.Contains("最新", text);
    }

    [Fact]
    public void ShortDraftIsUntouched()
    {
        // 放得下就一个字都不能动
        Assert.Equal("> abc", InputLine.DraftTail("> ", "abc", 40));
    }

    [Fact]
    public void NeverExceedsTheWidthOnceThePrefixFits()
    {
        // 前缀占满整行时**仍然保留草稿**：草稿是用户正在编辑的内容，
        // 悄悄删掉它比超宽危险得多（用户会以为输入被清空了）。
        // 这是既有约定，所以从「前缀放得下」的宽度起扫。
        var prefixWidth = TextUtil.DisplayWidth("> ");
        for (var w = prefixWidth + 1; w <= 120; w++)
        {
            var text = InputLine.DraftTail("> ", new string('q', 300), w);
            Assert.True(TextUtil.DisplayWidth(text) <= w,
                $"宽度 {w}：草稿行 {TextUtil.DisplayWidth(text)} 列越界");
        }
    }

    [Fact]
    public void TheCursorStaysInsideTheScrolledWindow()
    {
        // 光标定位必须与渲染同一套算式：渲染从头部丢、光标却按全文宽度往左数，
        // 两者一旦不同源，光标就停在错的位置。
        var draft = new string('a', 80);
        var (col, width) = InputLine.CursorInWindow(draft, draft.Length, 30);
        Assert.True(col <= width, $"光标列 {col} 超出了窗口宽度 {width}");
        Assert.True(width <= 30, $"窗口宽度 {width} 越界");
    }
}
