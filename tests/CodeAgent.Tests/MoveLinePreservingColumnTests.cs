using System;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 多行输入里 ↑↓ 要**保持列位置**。
///
/// 原先 MoveLineUp/Down 只跳到行首/行尾：多行粘贴进来想改中间某行时，
/// 按 ↑↓ 每次都落到行首，等于每换一行都要重新数着按 →，
/// 实际根本没法用键盘改多行内容。
///
/// 列按**码点**计而不是 UTF-16 单元：一行里有 emoji 时，
/// 按 UTF-16 偏移定位会错位到半个代理对中间。
/// </summary>
public class MoveLinePreservingColumnTests
{
    private static EditableLine Line(string text, int cursor)
    {
        var line = new EditableLine();
        line.SetInitial(text);
        // SetInitial 把光标放在末尾。用 Home + 循环右移摆到目标位置--
        // 不能写死「右移 N 次」：MoveRight 把代理对当一个码点整体跳过，
        // 有 emoji 的行上按次数走会落到别处。
        line.Home();
        while (line.Cursor < cursor)
            line.MoveRight();
        return line;
    }

    [Fact]
    public void UpKeepsTheColumn()
    {
        // "abc\ndef"：光标在 def 的第 2 列（e 后），↑ 应落到 abc 的第 2 列（b 后）
        var line = Line("abc\ndef", 6);
        Assert.Equal(6, line.Cursor);
        Assert.True(line.MoveLinePreservingColumn(up: true));
        Assert.Equal(2, line.Cursor);
    }

    [Fact]
    public void DownKeepsTheColumn()
    {
        var line = Line("abc\ndef", 2);
        Assert.True(line.MoveLinePreservingColumn(up: false));
        Assert.Equal(6, line.Cursor);
    }

    [Fact]
    public void ShortTargetLineClampsToItsEnd()
    {
        // 上一行只有 1 列，当前在第 5 列：落到上一行行尾而不是越界或回填
        var line = Line("a\nbcdef", 7);
        Assert.True(line.MoveLinePreservingColumn(up: true));
        Assert.Equal(1, line.Cursor);
    }

    [Fact]
    public void ColumnCountsCodePointsNotUtf16Units()
    {
        // "😀😀\nxxxx"：第一行两个 emoji（4 个 UTF-16 单元），光标在第 2 个 emoji 之后。
        // 按码点算是第 2 列，↓ 应落到 xxxx 的第 2 列（索引 7），而不是按 4 个单元落到行尾。
        var line = Line("😀😀\nxxxx", 4);
        Assert.Equal(4, line.Cursor);
        Assert.True(line.MoveLinePreservingColumn(up: false));
        Assert.Equal(7, line.Cursor);
    }

    [Fact]
    public void EmojiOnTheTargetLineIsNotSplit()
    {
        // 上一行以 emoji 开头，要落到它**之后**而不是插进代理对中间
        var line = Line("😀ok\nzz", 6);
        Assert.True(line.MoveLinePreservingColumn(up: true));
        Assert.Equal(2, line.Cursor); // 两个 UTF-16 单元之后
    }

    [Fact]
    public void FirstLineReportsNoMove()
    {
        // 已在首行：返回 false，交给外层当历史浏览（否则多行草稿会把 ↑ 彻底吃掉）
        var line = Line("abc\ndef", 1);
        Assert.False(line.MoveLinePreservingColumn(up: true));
        Assert.Equal(1, line.Cursor); // 原地不动
    }

    [Fact]
    public void LastLineReportsNoMove()
    {
        var line = Line("abc\ndef", 7);
        Assert.False(line.MoveLinePreservingColumn(up: false));
        Assert.Equal(7, line.Cursor);
    }

    [Fact]
    public void SingleLineDefersToHistoryBrowsing()
    {
        // 单行没有「行间」可言：必须返回 false，让外层去翻历史
        var line = Line("abc", 1);
        Assert.False(line.MoveLinePreservingColumn(up: true));
        Assert.False(line.MoveLinePreservingColumn(up: false));
    }

    [Fact]
    public void ThreeLinesWalkBothWaysAtTheSameColumn()
    {
        var line = Line("aaa\nbbb\nccc", 1);
        Assert.True(line.MoveLinePreservingColumn(up: false));
        Assert.Equal(5, line.Cursor);
        Assert.True(line.MoveLinePreservingColumn(up: false));
        Assert.Equal(9, line.Cursor);
        Assert.True(line.MoveLinePreservingColumn(up: true));
        Assert.Equal(5, line.Cursor);
        Assert.True(line.MoveLinePreservingColumn(up: true));
        Assert.Equal(1, line.Cursor);
    }
}
