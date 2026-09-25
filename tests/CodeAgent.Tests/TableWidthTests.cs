using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// Markdown 表格列宽收敛：单列上限之外再约束总宽，避免宽表在窄终端整行折行。
/// 总宽口径 = 缩进 2 + Σ列宽 + 3 × (列数-1)。
/// </summary>
public class TableWidthTests
{
    private const int Indent = 2;
    private const int Sep = 3;

    private static int Total(int[] widths) => Indent + widths.Sum() + Sep * (widths.Length - 1);

    [Fact]
    public void FitColumnWidths_NoBudget_LeavesWidthsUnchanged()
    {
        var input = new[] { 10, 20, 30 };
        Assert.Equal(input, ConsoleRenderer.FitColumnWidths(input, 0));
        Assert.Equal(input, ConsoleRenderer.FitColumnWidths(input, -1));
    }

    [Fact]
    public void FitColumnWidths_EmptyInput_Handled()
    {
        Assert.Empty(ConsoleRenderer.FitColumnWidths(Array.Empty<int>(), 40));
    }

    [Fact]
    public void FitColumnWidths_WideEnough_Unchanged()
    {
        var input = new[] { 8, 6, 4 };
        Assert.Equal(input, ConsoleRenderer.FitColumnWidths(input, 100));
    }

    [Fact]
    public void FitColumnWidths_ShrinksPeakColumnsUntilFits()
    {
        var result = ConsoleRenderer.FitColumnWidths(new[] { 40, 6, 4 }, 40);
        Assert.True(Total(result) <= 40, $"总宽 {Total(result)} 超出预算 40");
        // 窄列不该被削，只削最宽列
        Assert.Equal(6, result[1]);
        Assert.Equal(4, result[2]);
        Assert.True(result[0] < 40);
    }

    [Fact]
    public void FitColumnWidths_StopsAtMinimumColumnWidth()
    {
        // 预算极小：削到下限就停，宁可超预算也不再削到不可读
        var result = ConsoleRenderer.FitColumnWidths(new[] { 20, 20 }, 5);
        foreach (var w in result)
            Assert.True(w >= ConsoleRenderer.MinTableColWidth, $"列宽 {w} 低于下限");
    }

    [Fact]
    public void FitColumnWidths_TerminatesAndKeepsColumnCount()
    {
        var result = ConsoleRenderer.FitColumnWidths(new[] { 40, 40, 40, 40 }, 30);
        Assert.Equal(4, result.Length);
    }

    [Fact]
    public void FitColumnWidths_DoesNotMutateInput()
    {
        var input = new[] { 40, 8 };
        ConsoleRenderer.FitColumnWidths(input, 20);
        Assert.Equal(40, input[0]);
    }

    [Fact]
    public void FitColumnWidths_RealisticNarrowTerminal()
    {
        // 5 列各 20 列 → 自然总宽 2 + 100 + 12 = 114；40 列终端上必须收敛
        var widths = Enumerable.Repeat(20, 5).ToArray();
        Assert.True(Total(widths) > 40);
        var fitted = ConsoleRenderer.FitColumnWidths(widths, 40);
        Assert.True(Total(fitted) <= 40);
    }
}
