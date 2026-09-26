using System;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

[Collection("ConsoleOutput")]
public sealed class SpinnerWidthTests
{
    private static readonly TimeSpan Twelve = TimeSpan.FromSeconds(12);

    [Fact]
    public void Spinner_FitsWithinTerminalWidth()
    {
        // 回归：spinner 是 \r 原地重写的一行，折行会把输入块撑成两行，
        // 之后每次重绘的光标定位都偏。此前这一行完全没有宽度约束。
        foreach (var columns in new[] { 20, 24, 30, 34, 40, 60, 80, 120 })
        {
            var text = AgentClass.BuildSpinnerLine("*", Twelve, 340, AgentClass.SpinnerHint(columns), columns);
            Assert.True(TextUtil.DisplayWidth(text) <= columns,
                $"columns={columns} 实际宽 {TextUtil.DisplayWidth(text)} 超宽：{text}");
        }
    }

    [Fact]
    public void Spinner_KeepsAnimationFrameAboveAllElse()
    {
        // 没有帧的行读起来就像卡死了——那正是用户最需要反馈的时刻
        for (var columns = 2; columns <= 40; columns++)
        {
            var text = AgentClass.BuildSpinnerLine("*", Twelve, 999999, AgentClass.SpinnerHint(columns), columns);
            Assert.StartsWith("*", text);
        }
    }

    [Fact]
    public void Spinner_DropsHintFirstThenTokensThenElapsed()
    {
        var wide = AgentClass.BuildSpinnerLine("*", Twelve, 340, "esc 中断", 120);
        Assert.Contains("esc 中断", wide);
        Assert.Contains("340", wide);
        Assert.Contains("用时", wide);

        // 34 列以下没有提示
        var mid = AgentClass.BuildSpinnerLine("*", Twelve, 340, AgentClass.SpinnerHint(30), 30);
        Assert.DoesNotContain("中断", mid);

        // 再窄先丢 token
        var narrow = AgentClass.BuildSpinnerLine("*", Twelve, 340, null, 16);
        Assert.DoesNotContain("tokens", narrow);
    }

    [Fact]
    public void Spinner_HintOnlyAppearsWhenItFits()
    {
        Assert.Equal("⎋ 中断", AgentClass.SpinnerHint(34));
        Assert.Null(AgentClass.SpinnerHint(33));
        // 宽度未知时不猜
        Assert.Null(AgentClass.SpinnerHint(0));
    }

    [Fact]
    public void Spinner_UnknownWidthKeepsEverything()
    {
        // 宽度未知时宁可超宽也不要凭空少一段：用户看到的统计比宽度更重要
        var text = AgentClass.BuildSpinnerLine("*", Twelve, 340, "esc 中断", 0);
        Assert.Contains("esc 中断", text);
        Assert.Contains("340", text);
        Assert.Contains("用时", text);
    }

    [Fact]
    public void Spinner_CompactsLargeTokenCounts()
    {
        var text = AgentClass.BuildSpinnerLine("*", Twelve, 12345, null, 120);
        Assert.Contains("12.3K", text);
        Assert.DoesNotContain("12345", text);
    }

    [Fact]
    public void Spinner_SeparatorsAreConsistentAfterDropping()
    {
        // 丢段后不能留下双分隔符（读起来像空了一段）
        foreach (var columns in new[] { 14, 18, 22, 26, 30, 34, 50 })
        {
            var text = AgentClass.BuildSpinnerLine("*", Twelve, 340, AgentClass.SpinnerHint(columns), columns);
            var sep = SafeColor.Glyphs.SegmentSeparator;
            Assert.DoesNotContain(sep + " " + sep, text);
        }
    }

    [Fact]
    public void Spinner_ClearSequenceCoversActualWidth()
    {
        // 清行按实际宽度填：写死列数会在长文本后留下残字
        Assert.Equal("\r    \r", AgentClass.SpinnerClearSequence(4));
        Assert.Equal("\r \r", AgentClass.SpinnerClearSequence(0));
    }
}
