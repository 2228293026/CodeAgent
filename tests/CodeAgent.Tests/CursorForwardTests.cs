using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入行光标右移的 ANSI 序列生成。
/// 回归点：ECMA-48 规定 CSI 的参数**省略或为 0 都按 1 处理**，所以
/// `\x1b[0C` 会把光标**右移 1 列**，而不是原地不动。此前两处光标定位
/// （折叠视图 / 多行输入）都直接拼 `\x1b[{col}C`，而空提示符 + 空内容
/// （或整行只有零宽字符）时 col 恰好是 0 —— 光标凭空偏一列。
/// 同一规则早已在光标上移处注明（CUU），但没有套用到右移。
/// </summary>
public class CursorForwardTests
{
    [Fact]
    public void ZeroEmitsNothing()
    {
        // 关键不变式：n=0 绝不能产出 "\x1b[0C"（那会让光标右移 1 列）
        Assert.Equal(string.Empty, InputLine.CursorForward(0));
    }

    [Fact]
    public void NegativeEmitsNothing()
    {
        Assert.Equal(string.Empty, InputLine.CursorForward(-1));
        Assert.Equal(string.Empty, InputLine.CursorForward(-100));
    }

    [Fact]
    public void OneEmitsAPlainSpace()
    {
        // 1 列用一个空格即可，所有终端都认，不必发转义序列
        Assert.Equal(" ", InputLine.CursorForward(1));
    }

    [Fact]
    public void LargerUsesTheEscapeSequence()
    {
        Assert.Equal("\x1b[5C", InputLine.CursorForward(5));
        Assert.Equal("\x1b[120C", InputLine.CursorForward(120));
    }

    [Fact]
    public void NeverEmitsAZeroParameterSequence()
    {
        for (var n = 0; n <= 20; n++)
            Assert.DoesNotContain("[0C", InputLine.CursorForward(n));
    }

    [Fact]
    public void EmptyPromptAndEmptyContentMoveZeroColumns()
    {
        // 空提示符 + 空输入行：光标列 = 0 + 0 = 0，不该发任何右移序列
        var col = TextUtil.DisplayWidth("") + TextUtil.DisplayWidth("");
        Assert.Equal(0, col);
        Assert.Equal(string.Empty, InputLine.CursorForward(col));
    }

    [Fact]
    public void ZeroWidthOnlyLineMovesZeroColumns()
    {
        // 零宽字符行（组合记号/变体选择符）同样不占列，col 仍是 0
        var col = TextUtil.DisplayWidth("️");
        Assert.Equal(0, col);
        Assert.Equal(string.Empty, InputLine.CursorForward(col));
    }

    [Fact]
    public void CjkPromptStillMovesCorrectly()
    {
        // 中文提示符占 2 列/字，列数不能按字符数算
        var col = TextUtil.DisplayWidth("> ");
        Assert.Equal(2, col);
        Assert.Equal("\x1b[2C", InputLine.CursorForward(col));
    }
}
