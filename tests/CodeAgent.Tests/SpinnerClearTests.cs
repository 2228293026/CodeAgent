using System;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// spinner 行的清行序列。
/// 回归点：此前写死 60 个空格，长标签（如「上下文超限，正在压缩历史… 已用时 1 分 23 秒」）
/// 超过 60 列时清不干净，屏幕上会留下半截残字。
/// </summary>
public class SpinnerClearTests
{
    [Fact]
    public void SpinnerClearSequence_ClearsExactlyTheLastFrameWidth()
    {
        var seq = AgentClass.SpinnerClearSequence(46);
        Assert.Equal("\r" + new string(' ', 46) + "\r", seq);
    }

    [Fact]
    public void SpinnerClearSequence_LongLabelIsFullyCleared()
    {
        // label 是无上限的入参：一旦有人传入更长的文案，写死 60 就会留下残字
        var label = "正在为这个很长的会话执行历史压缩，压缩期间会持续统计 token 与耗时";
        var frame = $"⠦ {label} 已用时 1 分 23 秒";
        var width = TextUtil.DisplayWidth(frame);
        Assert.True(width > 60, $"测试前提：这条帧应超过 60 列，实际 {width}");
        var seq = AgentClass.SpinnerClearSequence(width);
        // 清行宽度必须覆盖整帧，否则残字留在屏幕上
        Assert.Equal(width, seq.Length - 2);
    }

    [Fact]
    public void SpinnerClearSequence_MeasuresDisplayWidthNotChars()
    {
        // 30 个汉字 = 60 列但只有 30 个字符：按字符数清会少清一半
        var cjk = new string('中', 30);
        var seq = AgentClass.SpinnerClearSequence(TextUtil.DisplayWidth(cjk));
        Assert.Equal(60, seq.Length - 2);
    }

    [Fact]
    public void SpinnerClearSequence_NeverEmitsEmptyLine()
    {
        // 宽度未知（0）时至少清 1 列，否则 \r\r 之间什么都不写，旧帧可能残留
        Assert.Equal("\r \r", AgentClass.SpinnerClearSequence(0));
        Assert.Equal("\r \r", AgentClass.SpinnerClearSequence(-5));
    }

    [Fact]
    public void SpinnerClearSequence_StartsAndEndsWithCarriageReturn()
    {
        var seq = AgentClass.SpinnerClearSequence(20);
        Assert.StartsWith("\r", seq);
        Assert.EndsWith("\r", seq);
    }

    [Fact]
    public void DefaultSpinnerFrameFitsLegacySixtyColumns()
    {
        // 默认帧较短，60 列的历史假设对它成立——本轮不改变短标签的观感
        var frame = $"⠦ 用时 1 分 23 秒 · ↑ 12.3K tokens";
        Assert.True(TextUtil.DisplayWidth(frame) <= 60, $"默认帧 {TextUtil.DisplayWidth(frame)} 列");
    }
}
