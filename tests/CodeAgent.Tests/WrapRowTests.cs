using System;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入文本**超过终端列宽**时的换行计入。
///
/// \x1b[2K 只清**一行**。此前重绘块行数只数文本里的显式 \n，
/// 完全没算终端的自动折行——于是输入敲过列宽之后：
///   · 屏幕已经占了 2 行，重绘却按 1 行处理，只清掉第一行；
///   · 第二行留着上一帧的残字；
///   · 光标列按 (提示符宽 + 内容宽) 算，超出列宽后位置完全错。
/// 这与"提示符超宽折行"是同一类 bug 的另一半。
/// </summary>
public sealed class WrapRowTests
{
    private const string Prompt = "[code] > ";
    private const int Columns = 20;
    private const int PromptWidth = 10;

    [Fact]
    public void ShortTextIsOneRow()
    {
        Assert.Equal(1, InputLine.RenderedRows("abc", Columns, 0));
    }

    [Fact]
    public void TextExceedingColumnCountCountsTheWrappedRow()
    {
        // 10 列提示 + 15 列内容 = 25 列 > 20 → 占 2 个终端行
        Assert.Equal(2, InputLine.RenderedRows(new string('x', 15), Columns, PromptWidth));
    }

    [Fact]
    public void ExactlyFittingIsStillOneRow()
    {
        // 恰好等于列宽不折行（终端光标停在最后一列，要再多一个字符才换行）
        Assert.Equal(1, InputLine.RenderedRows(new string('x', Columns - PromptWidth), Columns, PromptWidth));
    }

    [Fact]
    public void OneMoreCharacterWraps()
    {
        Assert.Equal(2, InputLine.RenderedRows(new string('x', Columns - PromptWidth + 1), Columns, PromptWidth));
    }

    [Fact]
    public void ManyWrapsAccumulate()
    {
        // 10 + 200 = 210 列 / 20 = 11 行
        Assert.Equal(11, InputLine.RenderedRows(new string('x', 200), Columns, PromptWidth));
    }

    [Fact]
    public void ExplicitNewlinesAndWrapsBothCount()
    {
        // "abc\n" + 一段会折行的长内容
        var rows = InputLine.RenderedRows("abc\n" + new string('y', 35), Columns, 0);
        Assert.Equal(3, rows); // "abc" 占 1 行；35 列 / 20 = 2 行
    }

    [Fact]
    public void CjkCountsByDisplayWidthNotCharCount()
    {
        // 20 个汉字 = 20 字符 = 40 列 → 提示 10 列 + 40 列 = 50 列 / 20 = 3 行
        // 按字符数算会得到 1~2 行，实现立刻露馅
        Assert.Equal(3, InputLine.RenderedRows(new string('中', 20), Columns, PromptWidth));
    }

    [Fact]
    public void UnknownColumnsCountsOnlyExplicitNewlines()
    {
        // 宽度未知：只数显式换行，宁可多算也不猜（猜错会清掉不该清的行）
        Assert.Equal(1, InputLine.RenderedRows(new string('x', 500), 0, 0));
        Assert.Equal(2, InputLine.RenderedRows("a\nb", 0, 0));
    }

    [Fact]
    public void EmptyTextIsStillOneRow()
    {
        Assert.Equal(1, InputLine.RenderedRows("", Columns, PromptWidth));
    }

    [Fact]
    public void RedrawSequence_MultilineWhenTextWraps()
    {
        // 端到端：敲到超过列宽时，重绘必须走多行分支（逐行清），否则残字留着不清
        var text = Prompt + new string('x', 15); // 25 列 > 20
        var seq = InputLine.BuildRedrawAnsi(text, 1, 0, out var rows, Columns);
        Assert.Equal(2, rows);
        // rows 行之间是 rows-1 个换行（最后一行后面不该再写换行，否则会多占一行）
        Assert.Equal(1, seq.Count(c => c == '\n'));
        Assert.Equal(2, CountClearLines(seq));      // 2 次清行：折行出来的那行也要清
    }

    [Fact]
    public void RedrawSequence_SingleLineWhenItFits()
    {
        var text = Prompt + "abc";
        var seq = InputLine.BuildRedrawAnsi(text, 1, 0, out var rows, Columns);
        Assert.Equal(1, rows);
        Assert.Equal(0, seq.Count(c => c == '\n'));
    }

    [Fact]
    public void EveryWidthLeavesNoLeftoverRow()
    {
        // 端到端不变量：块的行数恒等于「终端实际会占的行数」，
        // 所以每一行都会被 \x1b[2K 清到——不留残字。
        for (var columns = 8; columns <= 120; columns++)
        {
            for (var len = 0; len <= 140; len++)
            {
                var text = new string('x', len);
                var rows = InputLine.RenderedRows(text, columns, 0);
                var expected = len <= 0 ? 1 : (len + columns - 1) / columns;
                Assert.True(Math.Abs(rows - expected) <= 1,
                    $"columns={columns} len={len} 行数 {rows} 与终端折行数 {expected} 差太多");
            }
        }
    }

    private static int CountClearLines(string seq)
    {
        var n = 0;
        for (var i = 0; i + 3 < seq.Length; i++)
            if (seq[i] == '\x1b' && seq[i + 1] == '[' && seq[i + 2] == '2' && seq[i + 3] == 'K')
                n++;
        return n;
    }
}
