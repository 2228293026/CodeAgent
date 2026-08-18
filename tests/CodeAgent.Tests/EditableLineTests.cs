using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class EditableLineTests
{
    [Fact]
    public void Insert_AppendsAtCursor()
    {
        var line = new EditableLine();
        line.Insert('a');
        line.Insert('b');
        Assert.Equal("ab", line.Text);
        Assert.Equal(2, line.Cursor);
    }

    [Fact]
    public void Insert_AtMiddle_PushesRestRight()
    {
        var line = new EditableLine();
        line.SetInitial("ac");
        line.MoveLeft();          // 光标在 a 与 c 之间
        line.Insert('b');
        Assert.Equal("abc", line.Text);
        Assert.Equal(2, line.Cursor);
    }

    [Fact]
    public void Backspace_RemovesCharBeforeCursor()
    {
        var line = new EditableLine();
        line.SetInitial("abc");
        line.MoveLeft(); // 光标在 b 与 c 之间
        Assert.True(line.Backspace());
        Assert.Equal("ac", line.Text);
        Assert.Equal(1, line.Cursor);
    }

    [Fact]
    public void Backspace_AtStart_DoesNothing()
    {
        var line = new EditableLine();
        line.SetInitial("abc");
        line.Home();
        Assert.False(line.Backspace());
        Assert.Equal("abc", line.Text);
        Assert.Equal(0, line.Cursor);
    }

    [Fact]
    public void Delete_RemovesCharAtCursor()
    {
        var line = new EditableLine();
        line.SetInitial("abc");
        line.MoveLeft();  // 光标在 b 与 c 之间
        line.MoveLeft();  // 光标在 a 与 b 之间
        Assert.True(line.Delete()); // 删除光标处的 b
        Assert.Equal("ac", line.Text);
        Assert.Equal(1, line.Cursor);
    }

    [Fact]
    public void Delete_AtEnd_DoesNothing()
    {
        var line = new EditableLine();
        line.SetInitial("abc");
        Assert.False(line.Delete());
        Assert.Equal("abc", line.Text);
    }

    [Fact]
    public void Movement_ClampsAtBoundaries()
    {
        var line = new EditableLine();
        line.SetInitial("abc");
        line.Home();
        Assert.Equal(0, line.Cursor);
        line.MoveLeft(); // 已在行首
        Assert.Equal(0, line.Cursor);
        line.End();
        Assert.Equal(3, line.Cursor);
        line.MoveRight(); // 已在行尾
        Assert.Equal(3, line.Cursor);
    }

    [Fact]
    public void HomeEnd_JumpToBoundaries()
    {
        var line = new EditableLine();
        line.SetInitial("hello world");
        line.MoveLeft();
        line.MoveLeft();
        line.Home();
        Assert.Equal(0, line.Cursor);
        line.End();
        Assert.Equal(11, line.Cursor);
    }

    [Fact]
    public void Replace_SetsTextAndMovesCursorToEnd()
    {
        var line = new EditableLine();
        line.SetInitial("old");
        line.Replace("new text");
        Assert.Equal("new text", line.Text);
        Assert.Equal(8, line.Cursor);
    }

    [Fact]
    public void Clear_EmptiesLine()
    {
        var line = new EditableLine();
        line.SetInitial("abc");
        line.Clear();
        Assert.Equal("", line.Text);
        Assert.Equal(0, line.Cursor);
        Assert.Equal(0, line.Length);
    }

    [Fact]
    public void SetInitial_NullOrEmpty_IsNoOp()
    {
        var line = new EditableLine();
        line.SetInitial(null);
        Assert.Equal("", line.Text);
        Assert.Equal(0, line.Cursor);
        line.SetInitial("");
        Assert.Equal(0, line.Cursor);
    }

    [Fact]
    public void BackspaceDelete_Sequence_BehavesLikeTextEditor()
    {
        // 模拟：输入 "abc"，光标移到中间，删 a 位置的 b，再插回，最终应还原
        var line = new EditableLine();
        line.SetInitial("abc");
        line.MoveLeft();           // a|c? 不：光标在 b 与 c 之间 → a b|c
        line.Backspace();          // 删 b → a|c
        line.Insert('b');          // 插回 → abc
        Assert.Equal("abc", line.Text);
        Assert.Equal(2, line.Cursor);
    }

    [Fact]
    public void CursorLeftOffset_PlainAscii()
    {
        // 光标在中间：从行尾左移到 cursor 处 = 剩余字符数
        Assert.Equal(0, InputLine.CursorLeftOffset("abc", 3)); // 光标在末尾：不移动
        Assert.Equal(2, InputLine.CursorLeftOffset("abc", 1)); // 剩 "bc" 两个字符列
        Assert.Equal(3, InputLine.CursorLeftOffset("abc", 0)); // 剩整行
    }

    [Fact]
    public void CursorLeftOffset_CjkCountsAsTwoColumns()
    {
        // 中文占 2 列："你好" = 4 列
        Assert.Equal(4, InputLine.CursorLeftOffset("你好", 0));   // 光标在行首：左移整行 4 列
        Assert.Equal(2, InputLine.CursorLeftOffset("你好", 1));   // 剩 "好" = 2 列
        Assert.Equal(3, InputLine.CursorLeftOffset("a你好b", 2)); // 总宽 1+2+2+1=6，光标前 "a你"=3 列 → 左移 3
    }

    [Fact]
    public void CursorLeftOffset_ClampsCursorToBounds()
    {
        Assert.Equal(0, InputLine.CursorLeftOffset("abc", 99));  // 越界 → 视为末尾，不移动
        Assert.Equal(3, InputLine.CursorLeftOffset("abc", -5));  // 负值 → 视为行首，左移整行
    }

    [Fact]
    public void CursorLeftOffset_EmojiCountsAsTwoColumns()
    {
        // 回归：emoji 是代理对（两个 char），曾按 4 列计算导致光标/表格对齐错位；
        // 终端实际按 2 列显示
        var emoji = "😀"; // U+1F600，两个 surrogate
        Assert.Equal(2, InputLine.CursorLeftOffset(emoji, 0));  // 整行 emoji = 2 列
        Assert.Equal(2, InputLine.CursorLeftOffset("a😀", 1));  // 总宽 1+2=3，前缀 "a"=1 → 左移 2
        Assert.Equal(3, InputLine.CursorLeftOffset("😀b", 0));  // 总宽 2+1=3，光标行首 → 左移 3
    }

    [Theory]
    [InlineData("abc", 5, "abc")]                    // 未超宽：原样
    [InlineData("abc", 3, "abc")]                    // 刚好：原样
    [InlineData("abcdef", 3, "ab…")]                 // 超宽：截断 + 省略号
    [InlineData("中文", 4, "中文")]                   // CJK 2 列，未超宽
    [InlineData("中文ab", 4, "中…")]                  // 回归：CJK 按 2 列计，曾按 1 字符算导致超宽
    [InlineData("中文abc", 6, "中文a…")]              // 2+2+1=5 ≤ 5，预留省略号列
    [InlineData("😀x", 3, "😀x")]                    // emoji(2)+x(1)=3 恰好上限：不截断
    [InlineData("😀xy", 3, "😀…")]                   // 2+1+1=4 > 3：截断，预留省略号列
    public void FitToWidth_TruncatesByDisplayWidth(string input, int width, string expected) =>
        Assert.Equal(expected, InputLine.FitToWidth(input, width));

    [Fact]
    public void MoveLineUp_JumpsToPreviousLineStart()
    {
        var line = new EditableLine();
        line.SetInitial("aa\nbb\ncc");
        line.End();                       // 光标在末尾（cc 后）
        Assert.True(line.MoveLineUp());   // 上一行（bb 行）行首
        Assert.Equal(3, line.Cursor);     // "aa\n" 之后 = bb 行首
        Assert.True(line.MoveLineUp());   // 再上一行（aa 行）行首
        Assert.Equal(0, line.Cursor);
        Assert.False(line.MoveLineUp());  // 已在首行：不再移动
    }

    [Fact]
    public void MoveLineUp_FromMiddleLine_GoesToLineStart()
    {
        var line = new EditableLine();
        line.SetInitial("aa\nbb\ncc");
        line.End(); // 光标在末尾（index 8）
        for (int i = 0; i < 4; i++)
            line.MoveLeft(); // 移到 index 4（bb 行内，第二个 b 前）
        Assert.Equal(4, line.Cursor);
        Assert.True(line.MoveLineUp()); // 上一行（aa 行）行首
        Assert.Equal(0, line.Cursor);
    }

    [Fact]
    public void MoveLineDown_JumpsToNextLineStart()
    {
        var line = new EditableLine();
        line.SetInitial("aa\nbb\ncc");
        line.Home();                      // 光标在开头
        Assert.True(line.MoveLineDown()); // 下一行（bb 行）行首
        Assert.Equal(3, line.Cursor);     // "aa\n" 之后
        Assert.True(line.MoveLineDown()); // 下一行（cc 行）行首
        Assert.Equal(6, line.Cursor);     // "aa\nbb\n" 之后
        Assert.True(line.MoveLineDown()); // 已在末行：移到行尾（无 \n）
        Assert.Equal(8, line.Cursor);     // 文本末尾
        Assert.False(line.MoveLineDown()); // 已在末尾：不再移动
    }

    [Fact]
    public void MoveLineUp_SingleLine_ClampsToStart()
    {
        var line = new EditableLine();
        line.SetInitial("abc");
        line.End();
        Assert.True(line.MoveLineUp()); // 单行无换行：移到行首
        Assert.Equal(0, line.Cursor);
        Assert.False(line.MoveLineUp()); // 已在行首
    }

    [Fact]
    public void MoveLineDown_SingleLine_ClampsToEnd()
    {
        var line = new EditableLine();
        line.SetInitial("abc");
        line.Home();
        Assert.True(line.MoveLineDown()); // 单行无换行：移到行尾
        Assert.Equal(3, line.Cursor);
        Assert.False(line.MoveLineDown()); // 已在行尾
    }

    private static EditableLine MakeLine(string text)
    {
        var b = new EditableLine();
        b.SetInitial(text);
        return b;
    }
}

public class WordNavTests
{
    [Fact]
    public void MoveWordLeft_SkipsSpacesThenWord()
    {
        var b = new EditableLine();
        b.SetInitial("hello  world");
        b.End();
        b.MoveWordLeft();
        Assert.Equal(7, b.Cursor); // 回到 world 的 w
        b.MoveWordLeft();
        Assert.Equal(0, b.Cursor); // 再回到 hello 的 h
        b.MoveWordLeft();
        Assert.Equal(0, b.Cursor); // 行首不动
    }

    [Fact]
    public void MoveWordRight_SkipsSpacesThenWord()
    {
        var b = new EditableLine();
        b.SetInitial("hello  world");
        b.Home(); // SetInitial 光标在末尾：先回到行首
        b.MoveWordRight();
        Assert.Equal(5, b.Cursor);
        b.MoveWordRight();
        Assert.Equal(12, b.Cursor); // 行尾
        b.MoveWordRight();
        Assert.Equal(12, b.Cursor);
    }

    [Fact]
    public void DeleteWordForward_RemovesWordAndTrailingSpaces()
    {
        var b = new EditableLine();
        b.SetInitial("hello  world");
        b.Home();
        Assert.True(b.DeleteWordForward());
        Assert.Equal("  world", b.Text); // 只删 hello（右侧空白留给下一词删除时清理）
        Assert.Equal(0, b.Cursor);
    }

    [Fact]
    public void DeleteWordBackward_RemovesWordAndLeadingSpaces()
    {
        var b = new EditableLine();
        b.SetInitial("hello  world");
        b.End();
        Assert.True(b.DeleteWordBackward());
        Assert.Equal("hello", b.Text); // 连同前导空格一起删
        Assert.Equal(5, b.Cursor);
        Assert.True(b.DeleteWordBackward());
        Assert.Equal("", b.Text); // 再删一次：最后一个词也被删掉
    }
    [Fact]
    public void WordOps_CjkText_TreatsRunAsOneWord()
    {
        // 连续中文（无空格）视为一个单词：跳/删整段
        var b = new EditableLine();
        b.SetInitial("你好 世界");
        b.End();
        b.MoveWordLeft();
        Assert.Equal(3, b.Cursor); // 「世」的位置（跨过空格后落在词首）
        b.End();
        Assert.True(b.DeleteWordBackward()); // 删「 世界」
        Assert.Equal("你好", b.Text);
    }
    [Fact]
    public void LineHome_LineEnd_StayWithinCurrentLine()
    {
        var line = new EditableLine();
        line.SetInitial("aa\nbb\ncc");
        line.Home();
        for (int i = 0; i < 5; i++) line.MoveRight(); // index 5（bb 行行尾的换行处）
        line.LineHome();
        Assert.Equal(3, line.Cursor); // bb 行首
        line.LineEnd();
        Assert.Equal(5, line.Cursor); // bb 行尾（\n 之前）
        line.Home(); // 全局 Home 不受影响
        Assert.Equal(0, line.Cursor);
        line.LineEnd(); // 首行没有后续… 在 aa 行内 → 行尾 index 2
        Assert.Equal(2, line.Cursor);
    }
}
