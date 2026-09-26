using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 历史列表在极窄终端下的角色前缀。
/// 回归点：角色前缀比正文预算还宽时，旧实现把前缀**整段丢掉**，
/// 这一行就成了无标签正文——列表里再也分不清哪条是用户、哪条是助手，
/// 而逐行可扫读正是历史列表存在的理由。
/// </summary>
public class HistoryRoleTagTests
{
    [Fact]
    public void WideBudgetKeepsTheFullTag()
    {
        var tag = Program.FitRoleTag("用户", false, 40);
        Assert.Equal("[用户] ", tag);
    }

    [Fact]
    public void ErrorRowsKeepTheirMark()
    {
        var tag = Program.FitRoleTag("用户", true, 40);
        Assert.Equal("[用户] ❗ ", tag);
    }

    [Fact]
    public void NarrowBudgetAbbreviatesButKeepsBrackets()
    {
        var tag = Program.FitRoleTag("用户", false, 6);
        // 不能丢掉整个前缀，但可以丢掉角色名的一部分
        Assert.StartsWith("[", tag);
        Assert.Contains("]", tag);
        Assert.Contains("…", tag);
        Assert.Contains("用", tag);
    }

    [Fact]
    public void VeryNarrowFallsBackToASingleMarker()
    {
        var tag = Program.FitRoleTag("用户", false, 2);
        Assert.Equal("· ", tag);
        var errorTag = Program.FitRoleTag("用户", true, 2);
        Assert.Equal("⚠ ", errorTag);
    }

    [Fact]
    public void ErrorAndNormalDifferEvenWhenAbbreviated()
    {
        // 缩写后仍要能区分正常行与错误行
        for (var budget = 3; budget <= 12; budget++)
        {
            Assert.NotEqual(
                Program.FitRoleTag("工具", false, budget),
                Program.FitRoleTag("工具", true, budget));
        }
    }

    [Fact]
    public void NeverExceedsTheBudget()
    {
        foreach (var budget in new[] { 2, 3, 4, 5, 6, 8, 10, 12, 20, 40 })
        {
            var tag = Program.FitRoleTag("很长的角色名称", true, budget);
            Assert.True(TextUtil.DisplayWidth(tag) <= budget,
                $"预算 {budget} 实际 {TextUtil.DisplayWidth(tag)}：{tag}");
        }
    }

    [Fact]
    public void HistoryLineKeepsATagOnVeryNarrowTerminals()
    {
        // 核心保证：整行预算极小时仍要留下可辨识的角色标识
        foreach (var width in new[] { 8, 10, 12, 16, 20 })
        {
            var line = Program.FormatHistoryLine("用户", "这是消息内容", false, width, tagWidth: 0);
            Assert.True(line.TrimStart().Length > 0, $"宽度 {width} 下整行为空");
            Assert.True(line.Contains('[') || line.Contains('·') || line.Contains('⚠'),
                $"宽度 {width} 下丢失了角色标识：{line}");
        }
    }

    [Fact]
    public void HistoryLineNeverExceedsTheBudget()
    {
        foreach (var width in new[] { 8, 12, 16, 24, 40, 80 })
        {
            var line = Program.FormatHistoryLine("工具", "read_file 读取了很长的内容", true, width, tagWidth: 0);
            Assert.True(TextUtil.DisplayWidth(line) <= width,
                $"预算 {width} 实际 {TextUtil.DisplayWidth(line)}：{line}");
        }
    }

    [Fact]
    public void WideHistoryLineIsUnchanged()
    {
        var line = Program.FormatHistoryLine("用户", "你好", false, 80, tagWidth: 0);
        Assert.Equal("  [用户] 你好", line);
    }

    [Fact]
    public void ZeroBudgetReturnsEmptyTag()
    {
        Assert.Equal("", Program.FitRoleTag("用户", false, 0));
    }
}
