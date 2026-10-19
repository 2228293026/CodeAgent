using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 回合摘要里的**目标行**：<c>🎯 重构渲染层 · ⏵ 3/5 · 1m20s</c>。
///
/// 此前只有 <c>⏵⏵ 任务 2/5</c>——三个信息里缺了两个：
/// · 不说「在做什么」：那份信息只在 <c>/tasks</c> 面板里，得再敲一次命令才看得到
/// · 不说「花了多久」：长任务里分不清是刚跑起来还是卡了十分钟
///
/// 三段各自可降级，任何宽度下都不越界——这一行属于回合摘要，
/// 越界会把整段摘要顶乱。
/// </summary>
/// <remarks>
/// 在 <c>ConsoleOutput</c> 集合里：字形取自 <c>SafeColor.Glyphs</c>，
/// 它读的是进程级可替换的 <c>SafeColor.ReadEnv</c>。
/// </remarks>
[Collection("ConsoleOutput")]
public class TaskGoalLineTests
{
    [Fact]
    public void GoalLineCarriesNameProgressAndElapsed()
    {
        var line = Program.FormatTaskProgressLine(3, 5, 120, "重构渲染层", TimeSpan.FromSeconds(80));
        Assert.Contains("重构渲染层", line);
        Assert.Contains("3/5", line);
        Assert.Contains(TextUtil.FormatSessionTime(TimeSpan.FromSeconds(80)), line);
    }

    [Fact]
    public void CompletedShowsAnExplicitVerdictNotJustAFraction()
    {
        // 光看 5/5 还要自己换算成「做完了」
        var line = Program.FormatTaskProgressLine(5, 5, 120, "收尾", TimeSpan.FromSeconds(30));
        Assert.Contains("complete", line);
        Assert.Contains("5/5", line);
    }

    [Fact]
    public void IncompleteHasNoCompleteWord()
    {
        var line = Program.FormatTaskProgressLine(3, 5, 120, "写测试", TimeSpan.FromSeconds(30));
        Assert.DoesNotContain("complete", line);
    }

    [Fact]
    public void SubSecondElapsedIsNotShown()
    {
        // 「0s」是纯噪音：它既不能说明快，也不能说明慢
        var line = Program.FormatTaskProgressLine(1, 3, 120, "x", TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain("0s", line);
    }

    [Fact]
    public void ElapsedIsDroppedFirstWhenNarrow()
    {
        // 耗时是唯一「只描述过去」的段，最先走；名字与进度必须留着
        var full = Program.FormatTaskProgressLine(3, 5, 0, "重构渲染层", TimeSpan.FromSeconds(80));
        var narrow = Program.FormatTaskProgressLine(3, 5, TextUtil.DisplayWidth(full) - 1, "重构渲染层", TimeSpan.FromSeconds(80));
        Assert.DoesNotContain("1m", narrow);
        Assert.Contains("3/5", narrow);
    }

    [Fact]
    public void NeverExceedsWidthAtAnyWidth()
    {
        // 1..120 全扫：越界一次就够把回合摘要顶乱
        for (var w = 1; w <= 120; w++)
        {
            var line = Program.FormatTaskProgressLine(999, 1000, w, "一个相当长的任务描述用来试探宽度", TimeSpan.FromSeconds(3721));
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"宽度 {w}：目标行 {TextUtil.DisplayWidth(line)} 列越界 → [{line}]");
        }
    }

    [Fact]
    public void ZeroTotalMeansNoLineAtAll()
    {
        // 没有任务清单时不该凭空出现一行「0/0」
        Assert.Equal(string.Empty, Program.FormatTaskProgressLine(0, 0, 120, "x", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void GoalMarkIsOneVisualUnitAndHasAnAsciiFallback()
    {
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = n => n == "CODEAGENT_ASCII" ? "1" : null;
            var line = Program.FormatTaskProgressLine(1, 3, 120, "x", TimeSpan.FromSeconds(5));
            Assert.DoesNotContain("🎯", line);
            Assert.StartsWith("*", line, StringComparison.Ordinal);
        }
        finally { SafeColor.ReadEnv = saved; }
        // Unicode 下是 emoji（代理对）：宽度计算必须认代理对，按 1 列算会让整行差 1 列
        Assert.Equal(2, TextUtil.DisplayWidth("🎯"));
    }
}
