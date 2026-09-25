using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 键值行统一格式（/diag 等）。
/// 回归点：键列曾用手数空格对齐——`.codeagent 大小` 含中文（2 字符占 4 列）、
/// `IsOutputRedirected` 恰好 18 字符，冒号各自被挤到不同列，整列失去对齐。
/// </summary>
public class KeyValueLineTests
{
    /// <summary>冒号的**显示列**（不是字符下标）——含中文时两者不同，视觉对齐只看显示列。</summary>
    private static int ColonColumn(string line)
    {
        var i = line.IndexOf(':', StringComparison.Ordinal);
        return i < 0 ? -1 : TextUtil.DisplayWidth(line[..i]);
    }

    [Fact]
    public void FormatKeyValueLine_AlignsColonAcrossRows()
    {
        var rows = new[] { ("WindowWidth", "120"), ("IsOutputRedirected", "False") };
        var keyWidth = rows.Max(r => TextUtil.DisplayWidth(r.Item1));
        var colons = rows.Select(r => ColonColumn(Program.FormatKeyValueLine(r.Item1, r.Item2, keyWidth))).ToList();
        Assert.Equal(colons[0], colons[1]);
    }

    [Fact]
    public void FormatKeyValueLine_AlignsCjkKeyByDisplayWidth()
    {
        // 键名里有中文：2 个汉字占 4 列，按字符数补齐会让冒号左移 2 列
        var rows = new[] { ("TuiAnsi", "True"), (".codeagent 大小", "1.2 GiB") };
        var keyWidth = rows.Max(r => TextUtil.DisplayWidth(r.Item1));
        var colons = rows.Select(r => ColonColumn(Program.FormatKeyValueLine(r.Item1, r.Item2, keyWidth))).ToList();
        Assert.Equal(colons[0], colons[1]);
    }

    [Fact]
    public void FormatKeyValueLine_UnknownKeyWidthUsesOwnWidth() =>
        Assert.Equal("  a : 1", Program.FormatKeyValueLine("a", "1"));

    [Fact]
    public void FormatKeyValueLine_UnknownTotalWidthDoesNotTruncate()
    {
        var value = new string('x', 300);
        Assert.Equal($"  a{new string(' ', 9)} : {value}", Program.FormatKeyValueLine("a", value, 10));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    public void FormatKeyValueLine_NeverExceedsWidth(int width)
    {
        var rows = new[]
        {
            ("IsInputRedirected", "False"),
            ("OutputEncoding", "utf-8 (CP65001)"),
            (".codeagent 大小", "1.2 GiB（会话日志/导出/历史，可整目录删除）"),
        };
        var keyWidth = rows.Max(r => TextUtil.DisplayWidth(r.Item1));
        foreach (var row in rows)
        {
            var line = Program.FormatKeyValueLine(row.Item1, row.Item2, keyWidth, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
        }
    }

    [Fact]
    public void FormatKeyValueLine_TooNarrowForHeadStillStaysInWidth()
    {
        // 宽度连 "  k : " 都放不下时不应返回超宽行
        foreach (var width in new[] { 2, 4, 6 })
        {
            var line = Program.FormatKeyValueLine("key", "value", 10, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
        }
    }

    [Fact]
    public void FormatKeyValueLine_RealDiagShapeKeepsColonColumn()
    {
        var rows = new List<(string, string)>
        {
            ("IsInputRedirected", "False"),
            ("OutputEncoding", "utf-8 (CP65001)"),
            ("WindowWidth", "120"),
            ("CursorLeft/Top", "0/0"),
            ("IsOutputRedirected", "False"),
            (".codeagent 大小", "3.4 MiB（会话日志/导出/历史，可整目录删除）"),
        };
        var keyWidth = rows.Max(r => TextUtil.DisplayWidth(r.Item1));
        var colons = new HashSet<int>();
        foreach (var (key, value) in rows)
            colons.Add(ColonColumn(Program.FormatKeyValueLine(key, value, keyWidth, 100)));
        Assert.Single(colons);
    }
}
