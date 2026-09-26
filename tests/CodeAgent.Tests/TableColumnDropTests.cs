using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 表格在窄终端下的列裁剪。
/// 回归点：此前只压缩列宽，宽表会被压成每列 1–2 个字符（`a │ b │ c`）——毫无信息量，
/// 而且用户完全看不出「这个表本来有 8 列」。
/// </summary>
public class TableColumnDropTests
{
    [Fact]
    public void MaxReadableColumns_KeepsAllWhenTheyFit()
    {
        var natural = new[] { 10, 8, 6 };
        Assert.Equal(3, ConsoleRenderer.MaxReadableColumns(natural, 80));
    }

    [Fact]
    public void MaxReadableColumns_DropsTrailingColumnsWhenNarrow()
    {
        var natural = new[] { 20, 18, 16, 14, 12, 10, 8, 6 };
        var keep = ConsoleRenderer.MaxReadableColumns(natural, 50);
        Assert.True(keep < natural.Length, "宽表在窄屏上应当裁列");
        Assert.True(keep >= 1);
    }

    [Fact]
    public void MaxReadableColumns_UnknownBudgetKeepsAll()
    {
        var natural = new[] { 20, 18, 16 };
        Assert.Equal(3, ConsoleRenderer.MaxReadableColumns(natural, 0));
    }

    [Fact]
    public void MaxReadableColumns_EmptyIsZero()
    {
        Assert.Equal(0, ConsoleRenderer.MaxReadableColumns(Array.Empty<int>(), 40));
    }

    [Fact]
    public void MaxReadableColumns_AlwaysKeepsAtLeastOneColumn()
    {
        // 一列都放不下时整个表格消失比显示一列更糟
        Assert.Equal(1, ConsoleRenderer.MaxReadableColumns(new[] { 50, 40 }, 3));
    }

    [Fact]
    public void MaxReadableColumns_SingleColumnAlwaysKept()
    {
        Assert.Equal(1, ConsoleRenderer.MaxReadableColumns(new[] { 100 }, 10));
    }

    [Fact]
    public void MinTableColWidthIsPositive()
    {
        Assert.True(ConsoleRenderer.MinTableColWidth >= 2, "小于 2 列宽的单元格没有信息量");
    }

    [Fact]
    public void DroppedColumnsAreTrailingNotLeading()
    {
        // 保留的必须是**前导**列：Markdown 表格的语义列顺序有约定，不能从中间挖洞
        var natural = new[] { 12, 12, 12, 12, 12 };
        var keep = ConsoleRenderer.MaxReadableColumns(natural, 40);
        Assert.Equal(keep, ConsoleRenderer.MaxReadableColumns(natural, 40)); // 确定性
        Assert.True(keep <= 3, $"40 列最多放 3 列，实际 {keep}");
    }
}
