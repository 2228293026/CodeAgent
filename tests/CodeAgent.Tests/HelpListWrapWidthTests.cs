using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 帮助列表说明折行后的整行宽度。
/// 回归点：续行的固定前缀是 "  " + "    "（6 列），但折行预算仍按
/// `width - 缩进 - 命令列宽 - 间隔` 计算——等于给**续行**预留了命令列的宽度。
/// 结果每行折行说明渲染成 2 + 4 + (width - 2 - commandWidth - 2) = width + 2 - commandWidth 列，
/// 只要命令列宽于 2 列（永远成立，/help 就 5 列）就**整行溢出终端**。
/// </summary>
public class HelpListWrapWidthTests
{
    private static readonly Program.HelpEntry[] Entries =
    [
        new("/help", "显示帮助"),
        new("/model", "查看或切换模型，这是一段比较长的说明文字用于触发折行"),
        new("/tools", "列出全部可用工具及其说明，同样很长需要折行处理"),
    ];

    [Fact]
    public void WrappedLinesNeverExceedTheWidth()
    {
        foreach (var width in new[] { 30, 40, 50, 60, 80, 100 })
        {
            var text = Program.FormatHelpList(Entries, width);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                Assert.True(TextUtil.DisplayWidth(trimmed) <= width,
                    $"预算 {width} 实际 {TextUtil.DisplayWidth(trimmed)}：{trimmed}");
            }
        }
    }

    [Fact]
    public void VeryNarrowTerminalNeverOverflows()
    {
        foreach (var width in new[] { 8, 12, 16, 20, 24 })
        {
            var text = Program.FormatHelpList(Entries, width);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                Assert.True(TextUtil.DisplayWidth(trimmed) <= width,
                    $"预算 {width} 实际 {TextUtil.DisplayWidth(trimmed)}：{trimmed}");
            }
        }
    }

    [Fact]
    public void UnknownWidthDoesNotWrap()
    {
        // 宽度未知：保持单列，不猜测折行位置
        var text = Program.FormatHelpList(Entries, 0);
        Assert.Equal(3, text.Split('\n').Length);
    }

    [Fact]
    public void LongCommandNameIsTruncated()
    {
        var longName = new[] { new Program.HelpEntry("dependency_lockfile_report", "说明") };
        var text = Program.FormatHelpList(longName, 20);
        foreach (var line in text.Split('\n'))
            Assert.True(TextUtil.DisplayWidth(line.TrimEnd('\r')) <= 20, line);
    }

    [Fact]
    public void ContinuationLinesAreIndented()
    {
        var text = Program.FormatHelpList(Entries, 40);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Contains("    ", text); // 续行带缩进
        Assert.All(lines, l => Assert.True(l.Length == 0 || l.StartsWith("  ", StringComparison.Ordinal)));
    }

    [Fact]
    public void DescriptionContentSurvivesWrapping()
    {
        var text = Program.FormatHelpList(Entries, 40);
        Assert.Contains("折行", text);
        Assert.Contains("帮助", text);
    }

    [Fact]
    public void EmptyEntriesProduceEmptyText()
    {
        Assert.Equal(string.Empty, Program.FormatHelpList(Array.Empty<Program.HelpEntry>(), 40));
    }

    [Fact]
    public void CjkDescriptionIsMeasuredByDisplayWidth()
    {
        var entries = new[] { new Program.HelpEntry("/x", "这是一段纯中文的说明文字内容需要折行") };
        foreach (var width in new[] { 16, 24, 32 })
        {
            var text = Program.FormatHelpList(entries, width);
            foreach (var line in text.Split('\n'))
                Assert.True(TextUtil.DisplayWidth(line.TrimEnd('\r')) <= width, $"预算 {width}：{line}");
        }
    }
}
