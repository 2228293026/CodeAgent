using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /history 列表渲染。
/// 回归点：①maxColumns 写死 300 列，80 列终端上一行铺满整屏；
/// ②角色前缀宽度不一（[用户] 与 [工具read_file]），正文起始列逐行跳动，列表无法扫读。
/// </summary>
public class HistoryListRenderTests
{
    [Fact]
    public void HistoryTagWidth_TakesWidestTag()
    {
        var tags = new[] { "[用户] ", "[助手] ", "[工具read_file] " };
        Assert.Equal(TextUtil.DisplayWidth("[工具read_file] "), Program.HistoryTagWidth(tags));
    }

    [Fact]
    public void HistoryTagWidth_EmptyIsZero() => Assert.Equal(0, Program.HistoryTagWidth(Array.Empty<string>()));

    [Fact]
    public void FormatHistoryLine_AlignsContentColumnAcrossRows()
    {
        var tags = new[] { "[用户] ", "[工具read_file] " };
        var width = Program.HistoryTagWidth(tags);
        var user = Program.FormatHistoryLine("用户", "内容一", false, 80, width);
        var tool = Program.FormatHistoryLine("工具read_file", "内容二", false, 80, width);
        Assert.Equal(ContentColumn(user), ContentColumn(tool));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void FormatHistoryLine_NeverExceedsWidth(int width)
    {
        foreach (var line in new[]
        {
            Program.FormatHistoryLine("用户", new string('中', 400), false, width),
            Program.FormatHistoryLine("工具read_file", new string('x', 400), false, width),
            Program.FormatHistoryLine("工具read_file", new string('x', 400), true, width),
        })
        {
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
        }
    }

    [Fact]
    public void FormatHistoryLine_NarrowerThanTagStillFits()
    {
        var line = Program.FormatHistoryLine("工具read_file", "内容", false, 8, 20);
        Assert.True(TextUtil.DisplayWidth(line) <= 8, $"溢出: {line}");
    }

    [Fact]
    public void FormatHistoryLine_ErrorMarkerStillPresent()
    {
        var line = Program.FormatHistoryLine("工具read_file", "失败", true, 80, 0);
        Assert.Contains("❗", line);
    }

    [Fact]
    public void FormatHistoryLine_DefaultBudgetKeepsLegacyCap()
    {
        // 宽度未知时仍用 300 列兜底（既有行为不变），而不是不设限
        var line = Program.FormatHistoryLine("用户", new string('中', 1000), false);
        Assert.True(TextUtil.DisplayWidth(line) <= Program.HistoryContentColumns + 8, $"超兜底预算: {TextUtil.DisplayWidth(line)}");
    }

    private static int ContentColumn(string line)
    {
        var body = line[2..]; // 去掉两空格缩进
        var end = body.IndexOf("] ", StringComparison.Ordinal);
        return end < 0 ? -1 : TextUtil.DisplayWidth(body[(end + 2)..]) + (end + 2);
    }
}
