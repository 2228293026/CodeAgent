using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 滚动模式下菜单的**编号**与**数字键选取**必须指向同一项。
///
/// 两边过去各算各的：展示方 <c>Take(9)</c> 从 0 开始编号，选取方
/// <c>menuOffset + n - 1</c>。今天 menuOffset 恒为 0 所以看不出来，
/// 但只要滚动模式真的滚动，屏幕上写的「3)」和按下 3 选中的就不是同一项——
/// 用户按下去的东西和看到的不一样，且没有任何提示能解释这个偏差。
/// </summary>
public sealed class MenuNumberingTests
{
    private static List<(string Name, string Desc)> Items(int n) =>
        Enumerable.Range(1, n).Select(i => ($"/cmd{i}", "说明")).ToList();

    [Fact]
    public void Window_NumbersStartAtOne()
    {
        var w = InputLine.FilterScrollWindow(Items(5), 0);
        Assert.Equal(1, w[0].Number);
        Assert.Equal(5, w.Count);
    }

    [Fact]
    public void Window_RespectsTheNineKeyLimit()
    {
        Assert.Equal(9, InputLine.FilterScrollWindow(Items(50), 0).Count);
    }

    [Fact]
    public void Window_EmptyWhenNoItems()
    {
        Assert.Empty(InputLine.FilterScrollWindow(Array.Empty<(string, string)>(), 0));
    }

    [Fact]
    public void Window_ClampsOffsetIntoRange()
    {
        var items = Items(20);
        Assert.NotEmpty(InputLine.FilterScrollWindow(items, -5));
        Assert.NotEmpty(InputLine.FilterScrollWindow(items, 999));
    }

    [Fact]
    public void Window_FollowsTheScrollOffset()
    {
        var w = InputLine.FilterScrollWindow(Items(20), 9);
        Assert.Equal("/cmd10", w[0].Name);
        Assert.Equal(1, w[0].Number); // 编号是窗口内的位置，不是全局下标
    }

    [Fact]
    public void Numbering_MatchesWhatTheDigitKeySelects()
    {
        // 核心不变量：屏幕上「N)」那一项 == 按 N 选中的那一项
        var items = Items(30);
        for (var offset = 0; offset < 25; offset++)
        {
            var window = InputLine.FilterScrollWindow(items, offset);
            foreach (var (number, name) in window)
            {
                var selected = InputLine.DigitKeySelection(number, offset, 0, items.Count);
                Assert.True(selected >= 0, $"offset={offset} 编号 {number} 按下却选不中");
                Assert.Equal(name, items[selected].Name);
            }
        }
    }

    [Fact]
    public void Numbering_HoldsAtEveryOffsetWithoutSkipping()
    {
        var items = Items(30);
        for (var offset = 0; offset < 30; offset++)
        {
            var window = InputLine.FilterScrollWindow(items, offset);
            foreach (var (number, _) in window)
            {
                var sel = InputLine.DigitKeySelection(number, offset, 0, items.Count);
                // 窗口里的每一项都必须能被它显示的编号选中
                Assert.Equal(offset + number - 1, sel);
            }
        }
    }

    [Fact]
    public void Numbering_HoldsInAnsiModeToo()
    {
        // ANSI 模式 menuShown 有值，两边仍须一致
        var items = Items(20);
        const int shown = 9;
        for (var offset = 0; offset + shown <= items.Count; offset++)
        {
            var window = InputLine.FilterScrollWindow(items, offset, shown);
            foreach (var (number, name) in window)
            {
                var sel = InputLine.DigitKeySelection(number, offset, shown, items.Count);
                Assert.Equal(name, items[sel].Name);
            }
        }
    }

    [Fact]
    public void DigitKey_RejectsNumbersOutsideTheWindow()
    {
        Assert.Equal(-1, InputLine.DigitKeySelection(0, 0, 9, 20));
        Assert.Equal(-1, InputLine.DigitKeySelection(10, 0, 9, 20));
        Assert.Equal(-1, InputLine.DigitKeySelection(1, 0, 0, 0));
    }
}
