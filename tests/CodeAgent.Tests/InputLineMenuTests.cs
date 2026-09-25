using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 斜杠/模式菜单行的名称列对齐：按显示宽度而非字符数。
/// 回归点：旧实现用 {name,-16} 按字符补齐，中文命令名占 2 列却只补 1 空格，描述列整体右移错位。
/// </summary>
public class InputLineMenuTests
{
    [Fact]
    public void PadToDisplayWidth_Ascii_PadsToExactColumn()
    {
        Assert.Equal("read_file       ", InputLine.PadToDisplayWidth("read_file", 16));
        Assert.Equal("read_file", InputLine.PadToDisplayWidth("read_file", 9));
    }

    [Fact]
    public void PadToDisplayWidth_CjkCountsTwoColumns()
    {
        // 8 个中文字符 = 16 列，正好占满名称列，不应再补空格
        Assert.Equal("读取文件内容列表", InputLine.PadToDisplayWidth("读取文件内容列表", 16));
        // 4 个中文字符 = 8 列，补 8 个空格
        Assert.Equal("读取文件        ", InputLine.PadToDisplayWidth("读取文件", 16));
    }

    [Fact]
    public void PadToDisplayWidth_Overlong_TruncatesWithEllipsis()
    {
        var padded = InputLine.PadToDisplayWidth("a_very_long_command_name", 16);
        Assert.Equal(16, TextUtil.DisplayWidth(padded));
        Assert.EndsWith("…", padded, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMenuLine_CommandMenu_NumbersRows()
    {
        var line = InputLine.FormatMenuLine("read_file", "读取文件", 0, modePicker: false);
        Assert.Equal("  1) read_file        读取文件", line);
    }

    [Fact]
    public void FormatMenuLine_ModeMenu_HasNoNumber()
    {
        var line = InputLine.FormatMenuLine("code", "编码", 0, modePicker: true);
        Assert.Equal("  code             编码", line);
    }

    [Fact]
    public void FormatMenuLine_DescriptionColumnAlignsAcrossAsciiAndCjkNames()
    {
        var ascii = InputLine.FormatMenuLine("read_file", "D", 0, modePicker: false);
        var cjk = InputLine.FormatMenuLine("读取文件", "D", 0, modePicker: false);
        // 前缀宽度相同（"  1) " = 5 列），名称列都是 16 列，描述必须落在同一列
        Assert.Equal(TextUtil.DisplayWidth(ascii) - 1, TextUtil.DisplayWidth(cjk) - 1);
        Assert.Equal(5 + 16 + 1, TextUtil.DisplayWidth(ascii) - TextUtil.DisplayWidth("D"));
    }

    [Fact]
    public void FormatMenuLine_RespectsCustomNameWidth()
    {
        var line = InputLine.FormatMenuLine("ls", "D", 0, modePicker: false, nameWidth: 8);
        Assert.Equal("  1) ls       D", line);
        // 名称超出自定义列宽时按显示宽度截断，不会把描述挤到下一行
        var overlong = InputLine.FormatMenuLine("read_file", "D", 0, modePicker: false, nameWidth: 8);
        Assert.Equal("  1) read_fi… D", overlong);
    }
}
