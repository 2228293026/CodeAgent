using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 窄屏下菜单行的名称列优先裁剪。
/// 回归点：此前对整行做 FitToWidth，窄终端下名称列被切掉右补白，
/// 描述列随之整体错位——FormatMenuLine 刻意做的对齐（表格感）就没了，
/// 剩下的是几列看不出内容的碎片。
/// </summary>
public class MenuLineFitTests
{
    [Fact]
    public void WideBudget_KeepsNameColumnAndFullDescription()
    {
        var line = InputLine.FitMenuLine("read_file", "读取文件（支持 offset/limit）", 100, modePicker: false, visibleRow: 0);
        Assert.StartsWith("  1) read_file", line);
        Assert.Contains("读取文件（支持 offset/limit）", line);
    }

    [Fact]
    public void NarrowBudget_KeepsNameColumnAligned()
    {
        // 两个长短不一的名称，窄屏下描述列起始列必须一致
        var a = InputLine.FitMenuLine("read", "读取", 40, modePicker: false, visibleRow: 0);
        var b = InputLine.FitMenuLine("read_file_very_long_name", "读取", 40, modePicker: false, visibleRow: 1);
        var descColA = a.IndexOf("读取", StringComparison.Ordinal);
        var descColB = b.IndexOf("读取", StringComparison.Ordinal);
        Assert.Equal(descColA, descColB);
    }

    [Fact]
    public void NarrowBudget_LongNameIsTruncatedButStaysInColumn()
    {
        var longName = InputLine.FitMenuLine("an_extremely_long_command_name", "描述", 40, modePicker: false, visibleRow: 0);
        var shortName = InputLine.FitMenuLine("read", "描述", 40, modePicker: false, visibleRow: 0);
        Assert.Contains("…", longName);
        // 名称被截断后右补白仍保留 → 描述列起点与短名称行完全一致
        Assert.Equal(
            TextUtil.DisplayWidth(shortName[..shortName.IndexOf("描述", StringComparison.Ordinal)]),
            TextUtil.DisplayWidth(longName[..longName.IndexOf("描述", StringComparison.Ordinal)]));
    }

    [Fact]
    public void TinyBudgetForDescription_OmitsItRatherThanFragmenting()
    {
        // 只够放下编号+名称：宁可整列省略，也不要切出几个像内容的碎片
        var line = InputLine.FitMenuLine("read", "一段很长的描述文字", 26, modePicker: false, visibleRow: 0);
        Assert.EndsWith(" …", line);
        Assert.DoesNotContain("一段很长的描述文字", line);
    }

    [Fact]
    public void InsufficientBudgetForName_ReturnsEmptySoRowCanBeHidden()
    {
        Assert.Equal("", InputLine.FitMenuLine("read", "描述", 5, modePicker: false, visibleRow: 0));
        Assert.Equal("", InputLine.FitMenuLine("read", "描述", 0, modePicker: false, visibleRow: 0));
    }

    [Fact]
    public void ModePickerHasNoNumberPrefix()
    {
        var line = InputLine.FitMenuLine("plan", "只读规划", 60, modePicker: true, visibleRow: 0);
        Assert.StartsWith("  plan", line);
    }

    [Fact]
    public void MinMenuDescWidthIsUsable()
    {
        Assert.True(InputLine.MinMenuDescWidth >= 4, "少于 4 列的描述没有任何可读性");
    }

    [Fact]
    public void EveryLineFitsTheBudget()
    {
        // 各种宽度（含极窄）下都不得超预算——超了就会折行破坏 ANSI 原地布局
        foreach (var budget in new[] { 20, 26, 30, 40, 60, 80, 120 })
        {
            var line = InputLine.FitMenuLine("一个比较长的中文命令名", "一段比较长的中文描述文字", budget, modePicker: false, visibleRow: 2);
            Assert.True(TextUtil.DisplayWidth(line) <= budget,
                $"预算 {budget} 实际 {TextUtil.DisplayWidth(line)}：{line}");
        }
    }

    [Fact]
    public void AllRowsInANarrowMenuShareTheSameDescriptionColumn()
    {
        // 菜单块是多行绘制：任何一行的描述列起点不同，整块的表格感就毁了。
        // 用每行唯一的描述文本定位起始列。
        var budget = 44;
        var items = new (string Name, string Desc)[]
        {
            ("a", "描述甲"),
            ("bb", "描述乙甲乙"),
            ("cccccccccccccccc", "描述丙"),
        };
        var columns = new System.Collections.Generic.List<int>();
        for (var i = 0; i < items.Length; i++)
        {
            var line = InputLine.FitMenuLine(items[i].Name, items[i].Desc, budget, modePicker: false, visibleRow: i);
            var at = line.IndexOf(items[i].Desc, StringComparison.Ordinal);
            Assert.True(at > 0, $"第 {i} 行应完整显示描述：{line}");
            columns.Add(TextUtil.DisplayWidth(line[..at]));
        }
        Assert.True(columns.Distinct().Count() == 1, $"描述列起点应一致，实际 {string.Join(",", columns)}");
    }
}
