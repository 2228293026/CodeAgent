using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 光标定位也要把终端自动折行算进去。
///
/// 第 227 轮让**清行**数对了折行，但 <c>PositionCursor</c> 仍然只数显式 \n：
/// 敲到超过列宽时光标所在行算少了，上移的���数就不对，光标落在错误的行上。
/// 清得干净、位置却错了——屏幕上看起来就是"光标跳到了别处"。
/// </summary>
public sealed class CursorWrapRowTests
{
    private const string Prompt = "[code] > ";
    private const int Columns = 20;

    [Fact]
    public void NoWrap_CursorIsOnRowZero()
    {
        var (row, rows) = InputLine.CursorRowInBlock(Prompt, "abc", 3, Columns);
        Assert.Equal(0, row);
        Assert.Equal(1, rows);
    }

    [Fact]
    public void NoWrap_CursorMidTextStaysOnRowZero()
    {
        // 未折行时，无论光标在第几个字符，都在第 0 行
        var (row, rows) = InputLine.CursorRowInBlock(Prompt, "abcdef", 3, Columns);
        Assert.Equal(0, row);
        Assert.Equal(1, rows);
    }

    [Fact]
    public void Wrap_MovesCursorToSecondRow()
    {
        // 9 列提示 + 20 列内容 = 29 列 / 20 → 2 行；光标在内容末尾 = 第 1 行
        var (row, rows) = InputLine.CursorRowInBlock(Prompt, new string('x', 20), 20, Columns);
        Assert.Equal(2, rows);
        Assert.Equal(1, row);
    }

    [Fact]
    public void Wrap_CursorBeforeTheWrapStaysOnFirstRow()
    {
        var (row, _) = InputLine.CursorRowInBlock(Prompt, new string('x', 20), 5, Columns);
        Assert.Equal(0, row);
    }

    [Fact]
    public void ExplicitNewlineStillCounts()
    {
        var (row, rows) = InputLine.CursorRowInBlock(Prompt, "ab\ncd", 5, Columns);
        Assert.Equal(2, rows); // 2 个显式行，都没折行
        Assert.Equal(1, row);  // 光标在第二行末尾
    }

    [Fact]
    public void UnknownColumnsCountsOnlyExplicitNewlines()
    {
        var (row, rows) = InputLine.CursorRowInBlock(Prompt, new string('x', 500), 500, 0);
        Assert.Equal(1, rows);
        Assert.Equal(0, row);
    }

    [Fact]
    public void CursorIsClampedToTextLength()
    {
        // 越界的 cursor 不该让行数变成负数或越界
        var (_, rows) = InputLine.CursorRowInBlock(Prompt, "abc", 99, Columns);
        Assert.Equal(1, rows);
    }

    [Fact]
    public void CursorIsNeverBeyondTheBlock()
    {
        for (var len = 0; len <= 80; len++)
            for (var cursor = 0; cursor <= len; cursor++)
            {
                var text = new string('x', len);
                var (row, rows) = InputLine.CursorRowInBlock(Prompt, text, cursor, Columns);
                Assert.True(row >= 0, $"len={len} cursor={cursor} row={row} 为负");
                Assert.True(row < rows, $"len={len} cursor={cursor} row={row} 超出块行数 {rows}");
            }
    }

    [Fact]
    public void CjkWrapCountsByDisplayWidth()
    {
        // 9 列提示 + 20 个汉字(40 列) = 49 列 / 20 → 3 行，光标在末行
        var (row, rows) = InputLine.CursorRowInBlock(Prompt, new string('中', 20), 20, Columns);
        Assert.Equal(3, rows);
        Assert.Equal(2, row);
    }
}
