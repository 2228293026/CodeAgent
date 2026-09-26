using System;
using System.Collections.Generic;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 菜单名称列宽度。
/// 回归点：名称列此前**固定 16 列**。模式菜单的名称都很短（plan/auto/…），
/// 每行白白空出十几列，描述列被推到很靠右——宽终端上有一大片无意义空白。
/// 现在按实际最长的菜单项定宽；仍然固定上限 16，很长的名称照旧由
/// FitMenuLine 按预算收敛。
/// </summary>
public class MenuNameWidthTests
{
    [Fact]
    public void ShortNamesGetATightColumn()
    {
        Assert.Equal(4, InputLine.MenuNameWidth(new[] { "plan", "auto" }));
    }

    [Fact]
    public void ColumnFitsTheLongestName()
    {
        Assert.Equal(9, InputLine.MenuNameWidth(new[] { "plan", "auto-plan", "go" }));
    }

    [Fact]
    public void CjkNamesAreMeasuredByDisplayWidth()
    {
        // 四个汉字 = 8 列，不是 4 个字符
        Assert.Equal(8, InputLine.MenuNameWidth(new[] { "ab", "自动规划" }));
    }

    [Fact]
    public void LongNamesAreCapped()
    {
        var width = InputLine.MenuNameWidth(new[] { new string('x', 60) });
        Assert.Equal(InputLine.MaxMenuNameWidth, width);
    }

    [Fact]
    public void EmptyMenuGetsTheMinimum()
    {
        Assert.Equal(InputLine.MinMenuNameWidth, InputLine.MenuNameWidth(Array.Empty<string>()));
    }

    [Fact]
    public void NeverBelowTheMinimum()
    {
        Assert.Equal(InputLine.MinMenuNameWidth, InputLine.MenuNameWidth(new[] { "a" }));
    }

    [Fact]
    public void ZwjEmojiNameIsNotOverCounted()
    {
        // ZWJ 序列按 2 列算（不是 8 列）。2 列低于名称列下限 4，会被抬到下限——
        // 要防的是「被算成 8 列」把名称列撑虚。
        Assert.Equal(InputLine.MinMenuNameWidth, InputLine.MenuNameWidth(new[] { "👨‍👩‍👧" }));
        Assert.Equal(2, TextUtil.DisplayWidth("👨‍👩‍👧"));
    }

    [Fact]
    public void CustomCapIsRespected()
    {
        Assert.Equal(6, InputLine.MenuNameWidth(new[] { "a-very-long-name" }, 6));
    }

    [Fact]
    public void NarrowMenuSavesColumnsComparedToTheFixedSixteen()
    {
        // 回归断言：短名称菜单比旧行为至少省下 10 列
        var names = new[] { "plan", "auto", "go" };
        var derived = InputLine.MenuNameWidth(names);
        Assert.True(InputLine.MaxMenuNameWidth - derived >= 10,
            $"实际只省下 {InputLine.MaxMenuNameWidth - derived} 列");
    }

    [Fact]
    public void MenuLinesStillAlignWithTheDerivedColumn()
    {
        var names = new[] { "plan", "auto-plan", "go" };
        var width = InputLine.MenuNameWidth(names);
        var rows = new List<string>();
        foreach (var n in names)
            rows.Add(InputLine.FormatMenuLine(n, "说明", 0, modePicker: true, nameWidth: width));
        // 描述列起点必须一致：名称列 + 一个分隔空格
        foreach (var row in rows)
        {
            var at = row.IndexOf("说明", StringComparison.Ordinal);
            Assert.Equal(width + 3, at);
        }
    }

    [Fact]
    public void NarrowMenuRowsNeverExceedTheBudget()
    {
        var names = new[] { "plan", "auto", "go" };
        var width = InputLine.MenuNameWidth(names);
        foreach (var budget in new[] { 12, 20, 40 })
        {
            foreach (var n in names)
            {
                var line = InputLine.FitMenuLine(n, "一段比较长的描述文字", budget, modePicker: true, visibleRow: 0, nameWidth: width);
                Assert.True(TextUtil.DisplayWidth(line) <= budget, $"预算 {budget}：{line}");
            }
        }
    }
}
