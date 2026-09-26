using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 终端宽度测量的降级策略。
/// 回归点：此前读不到宽度（输出重定向 / Console.WindowWidth 抛异常）一律返回 0，
/// 而 0 的含义是**「未知、不截断」**。于是一次测量失败就让后续所有输出
/// 无限宽溢出——正是整套宽度预算存在的理由。
/// 另一个坑：终端最小化或拖拽过程中会短暂返回 1~2 列，拿它去裁剪会把
/// 每一行都截成只剩一个省略号。
/// </summary>
public class TerminalWidthResolveTests
{
    [Fact]
    public void PlausibleWidthIsUsedAsIs()
    {
        Assert.Equal(120, Program.ResolveColumns(120, 0));
    }

    [Fact]
    public void ReadFailureFallsBackToLastGood()
    {
        // 关键不变式：一次读不到，宽度**不能**变成 0（0 = 不截断 = 溢出）
        Assert.Equal(80, Program.ResolveColumns(0, 80));
    }

    [Fact]
    public void ImplausiblySmallWidthFallsBackToLastGood()
    {
        // 终端最小化/拖拽抖动返回 1~2 列时不能拿它裁剪
        Assert.Equal(80, Program.ResolveColumns(2, 80));
        Assert.Equal(80, Program.ResolveColumns(1, 80));
    }

    [Fact]
    public void ImplausiblySmallWidthWithNoHistoryReturnsMeasured()
    {
        // 没有历史好值时如实返回，宁可窄也不假装"未知"
        Assert.Equal(2, Program.ResolveColumns(2, 0));
    }

    [Fact]
    public void ReadFailureWithNoHistoryReturnsZero()
    {
        // 首轮就读不到：只能返回 0（未知），这是无法避免的
        Assert.Equal(0, Program.ResolveColumns(0, 0));
    }

    [Fact]
    public void NarrowButPlausibleTerminalIsRespected()
    {
        // 8 列虽然窄，但可信——窄屏用户就该看到窄屏布局
        Assert.Equal(Program.MinPlausibleColumns, Program.ResolveColumns(Program.MinPlausibleColumns, 0));
    }

    [Fact]
    public void JustBelowThresholdFallsBack()
    {
        Assert.Equal(40, Program.ResolveColumns(Program.MinPlausibleColumns - 1, 40));
    }

    [Fact]
    public void AbsurdlyWideIsClamped()
    {
        // 超宽屏上限制在 300 列，避免一行拉到几千列
        Assert.Equal(300, Program.ResolveColumns(1000, 0));
    }

    [Fact]
    public void ResizeIsFollowed()
    {
        // 窗口变小后应立刻采用新宽度，而不是被历史值粘住
        Assert.Equal(40, Program.ResolveColumns(40, 120));
    }

    [Fact]
    public void ThresholdIsSane()
    {
        Assert.True(Program.MinPlausibleColumns >= 4 && Program.MinPlausibleColumns <= 20);
    }

    [Fact]
    public void NeverExceedsThreeHundred()
    {
        foreach (var measured in new[] { 0, 1, 5, 8, 80, 500, 5000 })
            Assert.InRange(Program.ResolveColumns(measured, 0), 0, 300);
    }
}
