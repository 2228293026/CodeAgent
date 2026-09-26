using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 键值行在值放不下时的呈现。
/// 回归点：值预算为 0 时此前返回 `head.TrimEnd()`，屏幕上是一个**孤零零的冒号**：
///   键 : 
/// 读起来像「值是空的」，而真实情况是「值被终端宽度截掉了」。
/// 两者含义完全不同 —— 后者更值得警惕：用户可能据此以为配置真的没设。
/// 现在统一标「…」。
/// </summary>
public class KeyValueNoRoomTests
{
    [Fact]
    public void DanglingColonIsMarkedAsTruncated()
    {
        foreach (var width in new[] { 6, 7, 8, 9, 10, 12 })
        {
            var line = Program.FormatKeyValueLine("key", "someValue", keyWidth: 10, width: width);
            Assert.DoesNotContain("key:  ", line); // 不能是「键 + 冒号 + 一串空格」
            if (!line.Contains("someValue", StringComparison.Ordinal))
                Assert.Contains('…', line);
        }
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var width in new[] { 4, 6, 8, 10, 12, 20, 40, 80 })
        {
            var line = Program.FormatKeyValueLine("key", "someValue", keyWidth: 10, width: width);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
        }
    }

    [Fact]
    public void NoTrailingWhitespace()
    {
        // 截断提示行不该以空格结尾（复制出来会带一堆无意义空白）
        foreach (var width in new[] { 6, 8, 10, 12, 20 })
        {
            var line = Program.FormatKeyValueLine("key", "someValue", keyWidth: 10, width: width);
            Assert.Equal(line.TrimEnd(), line);
        }
    }

    [Fact]
    public void ValueSurvivesWhenItFits()
    {
        var line = Program.FormatKeyValueLine("key", "value", keyWidth: 10, width: 40);
        Assert.Contains("value", line);
        Assert.DoesNotContain('…', line);
    }

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        // 键列 = max(keyWidth, 键宽) = 10，缩进 2 + 键列 10 + " : " + 值
        var line = Program.FormatKeyValueLine("key", "value", keyWidth: 10, width: 0);
        Assert.Equal("  " + InputLine.PadToDisplayWidth("key", 10) + " : value", line);
        Assert.Equal(20, TextUtil.DisplayWidth(line));
    }

    [Fact]
    public void TruncatedValueIsMarked()
    {
        var line = Program.FormatKeyValueLine("key", new string('v', 100), keyWidth: 10, width: 20);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void CjkKeyIsMeasuredByDisplayWidth()
    {
        foreach (var width in new[] { 8, 10, 12, 16, 24 })
        {
            var line = Program.FormatKeyValueLine("配置项", "值", keyWidth: 8, width: width);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
        }
    }

    [Fact]
    public void EmptyValueIsNotMarkedAsTruncated()
    {
        // 值本来就是空的 ≠ 被截断：不该加省略号
        var line = Program.FormatKeyValueLine("key", "", keyWidth: 10, width: 40);
        Assert.DoesNotContain('…', line);
    }
}
