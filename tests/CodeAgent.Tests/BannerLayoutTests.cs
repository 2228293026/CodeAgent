using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 启动横幅的排版。
/// 两个真实缺陷：
/// ①手打空格对齐。`会话日志`（8 列）后补 2 空格落在第 10 列，
///   而 `Version`（7 列）补 2 空格落在第 9 列——**中文键把冒号列推歪了一格**。
///   这正是 FormatKeyValueLine 当初要解决的问题，横幅却一直没用它。
/// ②没有宽度预算。baseUrl / 工作区 / 会话日志路径都可能很长，
///   两条 58 列的分隔线更是固定宽度 —— 20 列终端上直接冲出去。
/// </summary>
public class BannerLayoutTests
{
    /// <summary>冒号所在的**显示列**。不能直接用 IndexOf(':')—那是字符下标，
    /// 中文键与 ASCII 键的下标根本不可比（这正是本轮要修的那个 bug 的测法）。</summary>
    private static int ColonColumn(string line)
    {
        var at = line.IndexOf(':', StringComparison.Ordinal);
        return TextUtil.DisplayWidth(line[..at]);
    }

    private static readonly string[] Keys = ["Version", "Provider", "Model", "Mode", "Thinking", "BaseUrl", "Workspace", "Git", "会话日志", "配置文件"];

    [Fact]
    public void EveryKeyLandsOnTheSameColonColumn()
    {
        var colons = Keys
            .Select(k => ColonColumn(Program.FormatKeyValueLine(k, "x", 9, 0)))
            .ToArray();
        Assert.Single(colons.Distinct());
    }

    [Fact]
    public void CjkKeyUsedToBeOffByOne()
    {
        // 回归点：手打空格时中文键落在 10、ASCII 键落在 9
        Assert.Equal(12, ColonColumn("  会话日志  : x"));
        Assert.Equal(11, ColonColumn("  Version  : x"));
    }

    [Fact]
    public void CjkAndAsciiKeysNowAgree()
    {
        Assert.Equal(
            ColonColumn(Program.FormatKeyValueLine("Version", "x", 9, 0)),
            ColonColumn(Program.FormatKeyValueLine("会话日志", "x", 9, 0)));
    }

    [Fact]
    public void EveryKeyColumnIsTheSameForAllKeys()
    {
        var cols = Keys.Select(k => ColonColumn(Program.FormatKeyValueLine(k, "value", 9, 0))).ToArray();
        Assert.All(cols, c => Assert.Equal(cols[0], c));
    }

    [Fact]
    public void BannerRowsNeverExceedTheWidth()
    {
        foreach (var width in new[] { 20, 40, 60, 80, 120 })
        {
            foreach (var key in Keys)
            {
                var line = Program.FormatKeyValueLine(key, new string('v', 80), 9, width);
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
            }
        }
    }

    [Fact]
    public void RuleLinesAreWidthBounded()
    {
        // 两条分隔线是固定 58/46 列的，20 列终端上会冲出去
        foreach (var width in new[] { 8, 12, 20, 40, 58 })
        {
            Assert.True(TextUtil.DisplayWidth(InputLine.FitToWidth(new string('─', 58), width)) <= width);
            Assert.True(TextUtil.DisplayWidth(InputLine.FitToWidth("── CodeAgent " + new string('─', 37), width)) <= width);
        }
    }

    [Fact]
    public void RuleLinesKeepTheirLookWhenWide()
    {
        Assert.Equal(new string('─', 58), InputLine.FitToWidth(new string('─', 58), 80));
        Assert.StartsWith("── CodeAgent ", InputLine.FitToWidth("── CodeAgent " + new string('─', 37), 80));
    }

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        var line = Program.FormatKeyValueLine("BaseUrl", "https://example.com", 9, 0);
        Assert.Equal("  " + InputLine.PadToDisplayWidth("BaseUrl", 9) + " : https://example.com", line);
    }

    [Fact]
    public void DeepPathValuesAreBounded()
    {
        var path = "C:/Users/me/AppData/Roaming/CodeAgent/sessions/2026-09-26/conversation-0001.jsonl";
        foreach (var width in new[] { 30, 40, 60 })
        {
            var line = Program.FormatKeyValueLine("会话日志", path, 9, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width, line);
            Assert.EndsWith("…", line);
        }
    }

    [Fact]
    public void BannerKeyWidthCoversTheLongestKey()
    {
        // Workspace 是最长的 ASCII 键；中文键更短但占两倍列
        Assert.True(Program.BannerKeyWidth >= TextUtil.DisplayWidth("Workspace"));
    }
}
