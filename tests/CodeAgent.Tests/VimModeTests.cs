using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// vim 普通模式的按键状态机（<c>/vim</c>）。
///
/// 这些用例守的是「按了某个键到底应该发生什么」，都是纯函数级断言，
/// 不碰终端，也不需要伪造 Console——状态机与渲染分离正是为了这个。
///
/// 特别值得钉住的三类：
///   · 普通模式下按键**不能落字**（否则 vim 等于没开）；
///   · 计数前缀（<c>3x</c>、<c>2dd</c>）与操作符（<c>d</c>/<c>c</c>）的组合；
///   · 代理对（emoji）不能被劈成半个码点。
/// </summary>
public class VimModeTests
{
    /// <summary>已经在普通模式的会话（Read 每次开始是插入模式，按 Esc 进普通）。</summary>
    private static VimSession Normal()
    {
        var session = new VimSession();
        session.Escape(string.Empty, 0);
        return session;
    }

    /// <summary>连续喂几个键，把每次的结果串起来。</summary>
    private static VimResult Run(VimSession session, string text, int cursor, params char[] keys)
    {
        var result = new VimResult(text, cursor, session.Mode, false);
        foreach (var key in keys)
            result = session.Feed(result.Text, result.Cursor, key);
        return result;
    }

    // ── 模式切换 ───────────────────────────────────────────────────────

    [Fact]
    public void StartsInInsertMode()
    {
        var session = new VimSession();

        Assert.Equal(VimInputMode.Insert, session.Mode);
    }

    [Fact]
    public void InsertModeNeverEatsKeys()
    {
        var session = new VimSession();

        // 插入模式下 h 就是个字母，不该被当命令吃掉。
        var result = session.Feed("abc", 3, 'h');

        Assert.False(result.Changed);
        Assert.Equal("abc", result.Text);
        Assert.Equal(3, result.Cursor);
    }

    [Fact]
    public void EscapeEntersNormalAndStepsCursorBack()
    {
        var session = new VimSession();

        var result = session.Escape("abc", 3);

        Assert.True(result.Changed);
        Assert.Equal(VimInputMode.Normal, session.Mode);
        Assert.Equal(2, result.Cursor); // vim：退出插入时光标左移一格
    }

    [Fact]
    public void EscapeAtBufferStartStaysPut()
    {
        var session = new VimSession();

        var result = session.Escape("abc", 0);

        Assert.Equal(0, result.Cursor);
    }

    [Fact]
    public void EscapeInNormalModeOnlyClearsPending()
    {
        var session = Normal();
        Run(session, "abcdef", 0, '2'); // 攒了一半的计数

        var result = session.Escape("abcdef", 0);

        Assert.False(result.Changed);
        Assert.Equal(VimInputMode.Normal, session.Mode);
    }

    // ── 移动 ───────────────────────────────────────────────────────────

    [Fact]
    public void HAndLMoveByCharacter()
    {
        var session = Normal();

        Assert.Equal(1, Run(session, "abc", 0, 'l').Cursor);
        Assert.Equal(1, Run(session, "abc", 2, 'h').Cursor);
    }

    [Fact]
    public void MotionsAreClampedToTheBuffer()
    {
        var session = Normal();

        Assert.Equal(0, Run(session, "abc", 0, 'h').Cursor);
        Assert.Equal(3, Run(session, "abc", 3, 'l').Cursor);
    }

    [Fact]
    public void ZeroAndDollarJumpWithinTheLine()
    {
        var session = Normal();

        Assert.Equal(0, Run(session, "abc", 2, '0').Cursor);
        Assert.Equal(3, Run(session, "abc", 1, '$').Cursor);
        // 多行：$ 只到**本行**行尾，不跨到下一行
        Assert.Equal(3, Run(session, "abc\ndef", 1, '$').Cursor);
    }

    [Fact]
    public void CaretSkipsLeadingBlanks()
    {
        var session = Normal();

        Assert.Equal(2, Run(session, "  abc", 4, '^').Cursor);
    }

    [Fact]
    public void WAndBWalkWords()
    {
        var session = Normal();

        Assert.Equal(4, Run(session, "foo bar", 0, 'w').Cursor);
        Assert.Equal(0, Run(session, "foo bar", 4, 'b').Cursor);
    }

    [Fact]
    public void EStopsOnTheLastCharacterOfTheWord()
    {
        var session = Normal();

        Assert.Equal(2, Run(session, "foo bar", 0, 'e').Cursor);
    }

    [Fact]
    public void CountPrefixRepeatsAMotion()
    {
        var session = Normal();

        Assert.Equal(3, Run(session, "abcdef", 0, '3', 'l').Cursor);
        Assert.Equal(0, Run(session, "abcdef", 3, '3', 'h').Cursor);
    }

    [Fact]
    public void GgAndGJumpToFirstAndLastLine()
    {
        var session = Normal();

        Assert.Equal(0, Run(session, "abc\ndef", 5, 'g', 'g').Cursor);
        Assert.Equal(4, Run(session, "abc\ndef", 0, 'G').Cursor);
    }

    [Fact]
    public void DanglingGPrefixIsDropped()
    {
        var session = Normal();

        var result = Run(session, "abc", 1, 'g', 'x');

        Assert.False(result.Changed); // g 后面跟了别的键：整条命令作废，不删任何东西
        Assert.Equal("abc", result.Text);
    }

    // ── 删除与修改 ─────────────────────────────────────────────────────

    [Fact]
    public void XDeletesTheCharacterUnderTheCursor()
    {
        var session = Normal();

        var result = Run(session, "abc", 1, 'x');

        Assert.True(result.Changed);
        Assert.Equal("ac", result.Text);
        Assert.Equal(1, result.Cursor);
    }

    [Fact]
    public void CountRepeatsX()
    {
        var session = Normal();

        Assert.Equal("def", Run(session, "abcdef", 0, '3', 'x').Text);
    }

    [Fact]
    public void XDoesNotCrossTheLineEnd()
    {
        var session = Normal();

        // 光标在本行，行尾就停：x 不该把下一行的第一个字吃掉
        Assert.Equal("a\ndef", Run(session, "abc\ndef", 1, '9', 'x').Text);
    }

    [Fact]
    public void DdClearsTheOnlyLine()
    {
        var session = Normal();

        var result = Run(session, "abc", 1, 'd', 'd');

        Assert.True(result.Changed);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(0, result.Cursor);
        Assert.Equal(VimInputMode.Normal, result.Mode);
    }

    [Fact]
    public void DdRemovesTheWholeLineIncludingItsNewline()
    {
        var session = Normal();

        Assert.Equal("def", Run(session, "abc\ndef", 1, 'd', 'd').Text);
    }

    [Fact]
    public void DdOnTheLastLineEatsThePrecedingNewline()
    {
        var session = Normal();

        // 不把上一行的换行吃掉，就会留下一个空行——看起来像「dd 没删干净」
        Assert.Equal("abc", Run(session, "abc\ndef", 5, 'd', 'd').Text);
    }

    [Fact]
    public void CountRepeatsDd()
    {
        var session = Normal();

        Assert.Equal("c", Run(session, "a\nb\nc", 0, '2', 'd', 'd').Text);
    }

    [Fact]
    public void DwDeletesThroughTheFollowingSpace()
    {
        var session = Normal();

        Assert.Equal("bar", Run(session, "foo bar", 0, 'd', 'w').Text);
    }

    [Fact]
    public void DbDeletesBackToThePreviousWordStart()
    {
        var session = Normal();

        Assert.Equal("bar", Run(session, "foo bar", 4, 'd', 'b').Text);
    }

    [Fact]
    public void CapitalDDeletesToLineEnd()
    {
        var session = Normal();

        var result = Run(session, "abc def", 3, 'D');

        Assert.Equal("abc", result.Text);
        Assert.Equal(VimInputMode.Normal, result.Mode); // D 不切插入模式
    }

    [Fact]
    public void CwChangesTheWordAndEntersInsert()
    {
        var session = Normal();

        var result = Run(session, "foo bar", 0, 'c', 'w');

        // cw 等价 ce：只换掉词本身，词后的空格留着
        Assert.Equal(" bar", result.Text);
        Assert.Equal(0, result.Cursor);
        Assert.Equal(VimInputMode.Insert, result.Mode);
    }

    [Fact]
    public void CcClearsTheLineAndEntersInsert()
    {
        var session = Normal();

        var result = Run(session, "abc", 1, 'c', 'c');

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(VimInputMode.Insert, result.Mode);
    }

    [Fact]
    public void CapitalCDeletesToLineEndAndEntersInsert()
    {
        var session = Normal();

        var result = Run(session, "abc def", 3, 'C');

        Assert.Equal("abc", result.Text);
        Assert.Equal(VimInputMode.Insert, result.Mode);
    }

    // ── 进入插入 ───────────────────────────────────────────────────────

    [Fact]
    public void InsertCommandsPlaceTheCursorWhereVimWould()
    {
        Assert.Equal(1, Run(Normal(), "abc", 1, 'i').Cursor);
        Assert.Equal(2, Run(Normal(), "abc", 1, 'a').Cursor);
        Assert.Equal(3, Run(Normal(), "abc", 1, 'A').Cursor);
        Assert.Equal(2, Run(Normal(), "  abc", 3, 'I').Cursor); // 首个非空白列，不是第 0 列
    }

    [Fact]
    public void LowercaseAAtBufferEndStaysPut()
    {
        Assert.Equal(3, Run(Normal(), "abc", 3, 'a').Cursor);
    }

    // ── 撤销 ───────────────────────────────────────────────────────────

    [Fact]
    public void UndoRestoresTheTextBeforeADeletion()
    {
        var session = Normal();
        var afterDelete = Run(session, "abc", 1, 'd', 'd');

        var undone = session.Feed(afterDelete.Text, afterDelete.Cursor, 'u');

        Assert.Equal("abc", undone.Text);
    }

    [Fact]
    public void UndoWithEmptyStackIsANoOp()
    {
        var session = Normal();

        var result = Run(session, "abc", 1, 'u');

        Assert.False(result.Changed);
        Assert.Equal("abc", result.Text);
    }

    [Fact]
    public void UndoAlsoCoversWhatWasTypedAfterEnteringInsert()
    {
        var session = Normal();
        Run(session, "abc", 0, 'i'); // 进入插入前压一次快照
        // 调用方在插入模式下把字打进了缓冲区（状态机不参与）
        session.Escape("Xabc", 1);

        var undone = session.Feed("Xabc", 1, 'u');

        Assert.Equal("abc", undone.Text);
    }

    // ── 杂项 ───────────────────────────────────────────────────────────

    [Fact]
    public void UnboundPrintableKeysAreSwallowed()
    {
        var session = Normal();

        // 普通模式下按 z：既不落字，也不报错
        var result = Run(session, "abc", 1, 'z');

        Assert.False(result.Changed);
        Assert.Equal("abc", result.Text);
    }

    [Fact]
    public void XOnAnEmojiRemovesTheWholeSurrogatePair()
    {
        var session = Normal();

        var result = Run(session, "\U0001F600a", 0, 'x');

        // 只删半个代理对会留下一个孤立代理，终端上渲染成乱码
        Assert.Equal("a", result.Text);
    }

    // ── 与渲染的契约 ───────────────────────────────────────────────────

    [Fact]
    public void BothModeMarksHaveTheSameDisplayWidth()
    {
        // 宽度一变，这一行的折行位置就变；而重绘靠「上移 N 行」定位，
        // 行数一变就会盖掉上一轮输出或留下残影。
        var insert = InputLine.VimModeMark(VimInputMode.Insert);
        var normal = InputLine.VimModeMark(VimInputMode.Normal);

        Assert.Equal(TextUtil.DisplayWidth(insert), TextUtil.DisplayWidth(normal));
        Assert.NotEqual(insert, normal);
    }

    // ── EditableLine.Reset ─────────────────────────────────────────────

    [Fact]
    public void ResetPutsTextAndCursorInOneStep()
    {
        var line = new EditableLine();

        line.Reset("hello", 2);

        Assert.Equal("hello", line.Text);
        Assert.Equal(2, line.Cursor);
        line.Insert('X');
        Assert.Equal("heXllo", line.Text);
    }

    [Fact]
    public void ResetClampsTheCursor()
    {
        var line = new EditableLine();

        line.Reset("abc", 99);

        Assert.Equal(3, line.Cursor);
    }

    [Fact]
    public void ResetNeverLeavesTheCursorBetweenSurrogates()
    {
        var line = new EditableLine();

        line.Reset("\U0001F600", 1); // 1 落在低代理上

        Assert.Equal(0, line.Cursor);
    }
}
