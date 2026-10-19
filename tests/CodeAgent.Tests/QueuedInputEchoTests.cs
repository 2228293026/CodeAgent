using System;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 回合中提前敲入的内容要**回声**。
///
/// 上一轮把它们攒进 _typeahead 不再丢弃，但攒下来是看不见的：
/// 用户敲完一整句，屏幕上除了 spinner 毫无变化，会以为按键又被吞了，于是停下来等——
/// 而他其实已经排上队了。这一小段「已排队 N 字」就是回执。
/// </summary>
/// <remarks>
/// 在 <c>ConsoleOutput</c> 集合里：字形来自 <c>SafeColor.Glyphs</c>（读进程级可替换的
/// <c>SafeColor.ReadEnv</c>）。
/// </remarks>
[Collection("ConsoleOutput")]
public class QueuedInputEchoTests
{
    private static string Frame => SafeColor.Glyphs.SpinnerFrame(0);

    [Fact]
    public void ShowsTheQueuedCountWhenSomethingIsWaiting()
    {
        var line = AgentClass.BuildSpinnerLine(
            Frame, TimeSpan.FromSeconds(12), 3400, "⎋ 中断", 120, queuedChars: 17);
        Assert.Contains("17", line);
        Assert.Contains("排队", line);
    }

    [Fact]
    public void NoEchoWhenNothingIsQueued()
    {
        // 没排队时多一句「已排队 0 字」纯属噪音
        var line = AgentClass.BuildSpinnerLine(
            Frame, TimeSpan.FromSeconds(12), 3400, "⎋ 中断", 120, queuedChars: 0);
        Assert.DoesNotContain("排队", line);
    }

    [Fact]
    public void QueuedEchoIsTheFirstThingDropped()
    {
        // 它是一句「我收到了」的客套；真正确定的确认是回合结束后输入框里出现的那些字。
        // 而提示回答「能不能停」、token/用时回答「跑了多久」——那两个更急。
        var wide = AgentClass.BuildSpinnerLine(
            Frame, TimeSpan.FromSeconds(3721), 128400, "⎋ 中断", 200, queuedChars: 42);
        Assert.Contains("排队", wide);
        // 逐步压窄，排队回执必须比中断提示先消失
        var narrow = AgentClass.BuildSpinnerLine(
            Frame, TimeSpan.FromSeconds(3721), 128400, "⎋ 中断", 40, queuedChars: 42);
        Assert.DoesNotContain("排队", narrow);
    }

    [Fact]
    public void NeverPushesTheSpinnerOverTheWidth()
    {
        // spinner 是 \r 原地重写的一行，折行会把输入块撑成两行、之后每次光标定位都偏
        for (var w = 1; w <= 120; w++)
        {
            var line = AgentClass.BuildSpinnerLine(
                Frame, TimeSpan.FromSeconds(3721), 128400, "⎋ 中断", w, queuedChars: 999);
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"宽度 {w}：spinner 行 {TextUtil.DisplayWidth(line)} 列越界 → [{line}]");
        }
    }

    [Fact]
    public void MultiLineQueueCountsEveryCharacter()
    {
        // 排队字数就是缓冲里的字符数：多行输入时换行也算（用户确实敲了那么多下）
        var prev = AgentClass.QueuedCharsProvider;
        try
        {
            Program.AppendTypeaheadForTest("第一行\n第二行");
            Assert.Equal(7, Program.QueuedChars());
            // 接上钩子后 spinner 能读到它
            AgentClass.QueuedCharsProvider = Program.QueuedChars;
            var line = AgentClass.BuildSpinnerLine(
                Frame, TimeSpan.FromSeconds(5), 100, null, 200, AgentClass.QueuedCharsProvider());
            Assert.Contains("7", line);
        }
        finally
        {
            AgentClass.QueuedCharsProvider = prev;
            // 清空缓冲，避免污染同集合里的其它测试
            Program.MergeTypeahead(null);
        }
    }
}
