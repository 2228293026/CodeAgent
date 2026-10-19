using System;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// Ctrl+U：删到本行行首（readline 里与 Ctrl+K 是**一对**）。
///
/// 只给 K 不给 U 是半套键盘：常用 readline/emacs 的人按 Ctrl+U 想清掉半个句子，
/// 得到的却是一个闪烁的 ^U（或者什么都不发生），只能改按一串 Backspace。
///
/// 两边的对称不只是「方向相反」，边界处理也必须一致：
/// 都不越过换行、都把代理对整对删掉。
/// </summary>
public class DeleteToLineStartTests
{
    private static EditableLine Line(string text, int cursor)
    {
        var line = new EditableLine();
        line.SetInitial(text);
        line.Home();
        while (line.Cursor < cursor)
            line.MoveRight();
        return line;
    }

    [Fact]
    public void DeletesToLineStart()
    {
        var line = Line("hello world", 5);
        Assert.True(line.DeleteToLineStart());
        Assert.Equal(" world", line.Text);
        Assert.Equal(0, line.Cursor);
    }

    [Fact]
    public void StopsAtTheNewlineAbove()
    {
        // 关键边界：不能把上一行的换行一起吃掉。没有 kill ring，
        // 删掉的换行再也找不回来--用户看到的是「按了一下 Ctrl+U，两行合成一行」。
        var line = Line("abc\ndefg", 7);
        Assert.True(line.DeleteToLineStart());
        Assert.Equal("abc\ng", line.Text);
        Assert.Equal(4, line.Cursor);
    }

    [Fact]
    public void AtLineStartDoesNothing()
    {
        // 行首时无动作：不做「删除上一行换行」那种附带效果
        var line = Line("abc\ndef", 4);
        Assert.False(line.DeleteToLineStart());
        Assert.Equal("abc\ndef", line.Text);
    }

    [Fact]
    public void AtAbsoluteStartDoesNothing()
    {
        var line = Line("abc", 0);
        Assert.False(line.DeleteToLineStart());
        Assert.Equal("abc", line.Text);
    }

    [Fact]
    public void DoesNotSplitASurrogatePair()
    {
        // 起点落在低代理上时，前一格的高代理属于同一码点，必须整对一起删。
        // 只删半个码点会留下孤立代理，终端渲染成乱码--而且是在删完之后才看得见。
        var line = Line("a😀b", 4);
        Assert.True(line.DeleteToLineStart());
        // "a" (1) + emoji (2) + "b" (1) = 4；删到行首后应全空
        Assert.Equal(string.Empty, line.Text);
        Assert.Equal(0, line.Cursor);
    }

    [Fact]
    public void MirrorsCtrlK()
    {
        // 同一段文本、同一个光标位置：Ctrl+U 删左半边，Ctrl+K 删右半边
        var left = Line("hello world", 5);
        left.DeleteToLineStart();
        Assert.Equal(" world", left.Text);

        var right = Line("hello world", 5);
        Assert.True(right.DeleteToLineEnd());
        Assert.Equal("hello", right.Text);
    }
}
