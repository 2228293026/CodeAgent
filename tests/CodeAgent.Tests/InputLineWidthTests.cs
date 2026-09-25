using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入行宽度鲁棒性：ANSI 原地渲染的降级判定与宽度预算。
/// 回归点：宽度未知（Console.WindowWidth 抛异常）时旧实现直接退回滚动式，
/// 且 Fit 预算退化为 10 列——两者叠加会让未知宽度的终端丢掉原地编辑体验。
/// </summary>
public class InputLineWidthTests
{
    [Theory]
    [InlineData(true, false, 120, true)]    // 正常宽终端
    [InlineData(true, false, 30, true)]     // 恰好等于最低宽度
    [InlineData(true, false, 29, false)]    // 明确过窄 → 滚动式
    [InlineData(true, false, 0, true)]      // 宽度未知 → 保留原地渲染
    [InlineData(false, false, 120, false)]  // 调用方要求滚动式
    [InlineData(true, true, 120, false)]    // 输出重定向
    [InlineData(true, true, 0, false)]      // 重定向优先于宽度未知
    public void ShouldUseAnsiInPlace_DecisionsAreExplicit(bool requested, bool redirected, int width, bool expected) =>
        Assert.Equal(expected, InputLine.ShouldUseAnsiInPlace(requested, redirected, width));

    [Theory]
    [InlineData(0, 0)]        // 宽度未知 → 0 = 不截断
    [InlineData(-1, 0)]       // 异常路径的负值同样按未知处理
    [InlineData(30, 26)]      // 窗口宽度 - 4
    [InlineData(80, 76)]
    [InlineData(6, 10)]       // 过窄时保底 10 列
    public void FitBudget_ReservesMarginAndFloorsAtTen(int width, int expected) =>
        Assert.Equal(expected, InputLine.FitBudget(width));

    [Fact]
    public void FitBudget_UnknownWidthMustNotCollapseToTen()
    {
        // 旧实现 Math.Max(10, winW - 4) 在 winW=0 时得到 10，会把长输入压成 10 列
        Assert.NotEqual(10, InputLine.FitBudget(0));
    }

    [Fact]
    public void AnsiMinWidth_MatchesDocumentedThreshold() =>
        Assert.Equal(30, InputLine.AnsiMinWidth);
}
