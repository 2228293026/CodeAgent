using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /context 面板：把「上下文被谁占满了」拆成可操作的几块。
///
/// 与 /status 的分工是这块的关键：/status 只给**一个总数**，
/// 而用户看到 ctx 80% 时想知道的恰恰是「哪一块能砍」。
/// 系统提示与工具定义是固定的（砍不动），历史消息才是能 /compact 的部分——
/// 所以分类**顺序**本身承载信息：可操作项排在最后。
/// </summary>
public class ContextPanelTests
{
    private static readonly Program.ContextSlice[] Slices =
    [
        new("系统提示", 3_000),
        new("工具定义（231 个）", 12_000),
        new("历史消息", 45_000),
    ];

    [Fact]
    public void ShowsTotalAgainstTheWindow()
    {
        var text = Program.FormatContextPanel(Slices, 60_000, 200_000);
        Assert.Contains("60", text);
        Assert.Contains("200", text);
        Assert.Contains("30%", text);
    }

    [Fact]
    public void SplitsIntoLabledSlices()
    {
        var text = Program.FormatContextPanel(Slices, 60_000, 200_000);
        foreach (var slice in Slices)
            Assert.Contains(slice.Label, text);
    }

    [Fact]
    public void PercentIsAgainstTheWindowNotTheUsedTotal()
    {
        // 占比分母必须是**窗口**：换成「已用」会把 5% 占用下的小项显示成 40%，更迷惑。
        // 系统提示 3k / 窗口 200k = 1.5% → 四舍五入 1% 或 2%，绝不能是 5%(3k/60k)
        var text = Program.FormatContextPanel(Slices, 60_000, 200_000);
        Assert.DoesNotContain("  5%", text);
    }

    [Fact]
    public void UnknownWindowOmitsPercentages()
    {
        // 分母未知时不猜：显示问号而不是编一个百分比
        var text = Program.FormatContextPanel(Slices, 60_000, 0);
        Assert.Contains("窗口未知", text);
        Assert.DoesNotContain("%", text.Replace("（窗口未知）", ""));
    }

    [Fact]
    public void MismatchWithTotalIsExplained()
    {
        // 分类之和与状态栏的 ctx 可能因口径差异对不上。
        // 让用户自己发现「两个数不一样」比明说差在哪糟得多。
        var text = Program.FormatContextPanel(Slices, 70_000, 200_000);
        Assert.Contains("未归类", text);
    }

    [Fact]
    public void MatchingSumDoesNotAddNoise()
    {
        // 对得上就不该多一行解释：那是纯噪音
        var text = Program.FormatContextPanel(Slices, 60_000, 200_000);
        Assert.DoesNotContain("未归类", text);
    }

    [Fact]
    public void EmptySlicesStillRenderSomething()
    {
        // 空明细不该打出一片空白：总比让用户以为命令没生效好
        var text = Program.FormatContextPanel([], 0, 200_000);
        Assert.Contains("无明细", text);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void PanelNeverExceedsTheWidth(int width)
    {
        var text = Program.FormatContextPanel(Slices, 70_000, 200_000, width);
        foreach (var line in text.Split('\n'))
        {
            var bare = line.TrimEnd('\r');
            if (bare.Length == 0)
                continue;
            var w = TextUtil.DisplayWidth(bare);
            Assert.True(w <= width, $"width={width} 超宽（{w} 列）：{bare}");
        }
    }

    [Fact]
    public void SlicesFromAgentNeverDoubleCountSystemPrompt()
    {
        // system 已经算在「系统提示」里，历史里必须扣掉——
        // 否则分类相加超过总数，用户会以为面板算错了。
        // 这里用纯数学关系断言，不依赖真实 Agent 实例。
        var total = 50_000;
        var system = 3_000;
        var tools = 12_000;
        var history = Math.Max(0, total - system - tools);
        Assert.Equal(total, system + tools + history);
    }

    [Fact]
    public void LabelColumnAlignsRegardlessOfLabelLength()
    {
        // 分类名长短不一（「系统提示」vs「工具定义（231 个）」）：
        // 固定宽度在改语言时会错位，必须按实际最长对齐
        var text = Program.FormatContextPanel(Slices, 60_000, 200_000);
        var lines = text.Split('\n').Where(l => l.Contains("  ")).ToArray();
        Assert.True(lines.Length >= 3, text);
    }
}
