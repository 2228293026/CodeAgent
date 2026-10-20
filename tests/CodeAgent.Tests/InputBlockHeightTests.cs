using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 展开态不能高过终端。宽度那条线查透了（折行、截断、边框），
/// 高度一条都没查过——而 ANSI 原地重绘的上移量 <c>\x1b[NA</c> 完全依赖
/// 「N 行还在屏幕里」。
///
/// 粘贴 200 行再按 ↓ 展开，块高就是 200 行，而终端只有 24 行：
/// 上移 200 行会停在屏幕顶行（终端不会越过顶行），块首根本到不了，
/// 旧内容残留、新内容叠上去——用户看到的是越用越乱的屏幕。
///
/// 修法取「放不下就不展开」而不是「展开后只显示一窗」：
/// 后者要同时改**画什么**与**光标映射到哪一行**，两处口径一错位就是光标跳到块外；
/// 而折叠视图（3 行）本来就是这块代码里唯一被验证过高度的形态。
/// 放不下时不展开，用键盘照样能在行间移动、编辑，只是不同时显示全部行。
/// </summary>
public class InputBlockHeightTests
{
    [Theory]
    [InlineData(5, 24, true)]    // 5 行放得进 24 行
    [InlineData(23, 24, true)]   // 24 行终端：块高最多 23（留一行给光标定位）
    [InlineData(24, 24, false)]  // 正好 24 行就已经放不下
    [InlineData(200, 24, false)] // 200 行在 24 行终端里怎么都放不下
    [InlineData(200, 50, false)]
    [InlineData(4, 5, true)]
    public void CanExpandOnlyWhenTheBlockFits(int lines, int terminalRows, bool expected)
        => Assert.Equal(expected, InputLine.CanExpand(lines, terminalRows));

    [Fact]
    public void UnknownHeightAllowsExpand()
    {
        // 行数未知（0）时不猜：交给终端软滚动。
        // 猜一个小值会让「输出重定向 / 读不到 WindowHeight」的环境里永远展不开。
        Assert.True(InputLine.CanExpand(200, 0));
    }

    [Fact]
    public void CollapsedBlockAlwaysFitsThreeRows()
    {
        // 折叠态的收益就是「再多行也只占 3 行」，这条不能退
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++)
            sb.Append(i == 0 ? "" : "\n").Append("line").Append(i);
        Assert.Equal(InputLine.FoldThreshold - 1, InputLine.DisplayedNewlines(expanded: false, sb.ToString()));
    }

    [Fact]
    public void CollapsedHeightIsIndependentOfLineCount()
    {
        // 200 行与 4 行折叠后都占 3 行：块高不随输入增长，上移量才有界
        var a = string.Join("\n", Enumerable.Range(0, 4).Select(i => $"l{i}"));
        var b = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"l{i}"));
        Assert.Equal(
            InputLine.DisplayedNewlines(expanded: false, a),
            InputLine.DisplayedNewlines(expanded: false, b));
    }
}
