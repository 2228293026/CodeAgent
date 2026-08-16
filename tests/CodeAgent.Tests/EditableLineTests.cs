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
}
