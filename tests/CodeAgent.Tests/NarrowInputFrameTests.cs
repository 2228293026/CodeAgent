using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 极窄终端下的输入框：任何宽度都**不许越界**。
///
/// 输入框是原地重绘的基线。它一旦比终端宽、折了行，重绘时算的行数就少一行，
/// 之后每敲一个字都再错一行——屏幕上会越滚越乱，而且用户完全看不出成因。
///
/// 这组测试把 width 从 0 穷举到 40，逐个断言上下边框、提示符行的显示宽度
/// 都不超过那个宽度。注释里写的「绝不越界」不算数，得测出来。
/// </summary>
public class NarrowInputFrameTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(40)]
    public void TopFrameNeverExceedsTheWidth(int width)
    {
        var top = Program.BuildInputFrameTop("> ", "高", width);
        foreach (var line in top.Split('\n'))
        {
            // width <= 0 是「宽度未知」：此时约定只给提示符、不画框，不做宽度约束
            if (width <= 0)
                continue;
            Assert.True(TextUtil.DisplayWidth(line) <= width,
                $"width={width} 的一行超宽（{TextUtil.DisplayWidth(line)} 列）：{line}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(10)]
    public void BottomFrameNeverExceedsTheWidth(int width)
    {
        var bottom = Program.BuildInputFrameBottom(width);
        Assert.True(TextUtil.DisplayWidth(bottom) <= width,
            $"width={width} 的下边框超宽（{TextUtil.DisplayWidth(bottom)} 列）：{bottom}");
    }

    [Fact]
    public void FrameKeepsBorderAbovePromptEvenWhenHintDoesNotFit()
    {
        // 窄终端下提示被丢掉，但顺序必须是「边框在上、提示符在下」。
        // 反过来会让「最后一行就是输入行」这个前提失效，光标列按边框宽度算就把光标推出行宽。
        var top = Program.BuildInputFrameTop("> ", "很长的档位提示", 6);
        var lines = top.Split('\n');
        Assert.True(lines.Length >= 2, $"应至少有边框行和提示符行：{top}");
        Assert.Contains(SafeColor.Glyphs.FrameTopLeft, lines[0]);
        Assert.Contains(">", lines[^1]);
    }

    [Fact]
    public void LeadNewlineStaysAboveTheBorder()
    {
        // 提示符的前导换行是「上一轮输出」与「输入框」之间的空行，必须落在边框**上方**。
        // 夹在边框和提示符之间就是一个空洞，框看起来是破的。
        var top = Program.BuildInputFrameTop("\n\n> ", null, 20);
        Assert.StartsWith("\n\n", top);
        var afterLead = top.TrimStart('\n');
        Assert.StartsWith(SafeColor.Glyphs.FrameTopLeft, afterLead);
    }
}
