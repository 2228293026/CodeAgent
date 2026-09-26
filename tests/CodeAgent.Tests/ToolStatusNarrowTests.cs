using System;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具状态行的窄屏降级。
/// 回归点：状态行刻意为耗时预留预算（budget = width - 耗时 - 标记），
/// 但预算算成负数时 `TrimToolSummaryArgs` 直接**返回完整摘要**——整行超宽，
/// 随后由 FitToWidth 截**尾部**，耗时被静默切掉。
/// 这与预留耗时的意图正好相反：极窄终端上只剩「✔ 摘要」而没有用时。
/// 正确降级是**丢摘要、留耗时**（工具身份通常在前一行 spinner 里可见）。
/// </summary>
public class ToolStatusNarrowTests
{
    private static readonly TimeSpan Elapsed = TimeSpan.FromMilliseconds(1234);

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        var line = AgentClass.FormatToolStatusLine("read_file(a.cs)", false, Elapsed, 0);
        Assert.Equal("  ✔ read_file(a.cs) (1.2s)", line);
    }

    [Fact]
    public void WideLineKeepsEverything()
    {
        var line = AgentClass.FormatToolStatusLine("read_file(a.cs)", false, Elapsed, 60);
        Assert.Contains("read_file", line);
        Assert.Contains("1.2s", line);
    }

    [Fact]
    public void VeryNarrowKeepsTheDuration()
    {
        // 关键不变式：只要「标记 + 耗时」物理上放得下，耗时就不能被切掉。
        // 放不下时（极窄）应整段去掉耗时，而不是切出 `(1.…` 这种半个数值——
        // 它看起来像个完整但不同的耗时，比没有更糟。
        foreach (var width in new[] { 11, 12, 14, 16, 18, 20, 30, 40 })
        {
            var line = AgentClass.FormatToolStatusLine("read_file(a.cs)", false, Elapsed, width);
            Assert.Contains("1.2s", line);
        }
    }

    [Fact]
    public void ImpossibleWidthDropsTheDurationWhole()
    {
        foreach (var width in new[] { 4, 6, 8, 10 })
        {
            var line = AgentClass.FormatToolStatusLine("read_file(a.cs)", false, Elapsed, width);
            Assert.DoesNotContain("(1.…", line);
            Assert.DoesNotContain("s)", line);
        }
    }

    [Fact]
    public void VeryNarrowKeepsTheMarker()
    {
        foreach (var width in new[] { 8, 10, 12, 16, 20 })
        {
            var line = AgentClass.FormatToolStatusLine("read_file(a.cs)", true, Elapsed, width);
            Assert.StartsWith("⚠", line.TrimStart());
        }
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var width in new[] { 6, 8, 10, 12, 16, 20, 30, 40, 60, 100 })
        {
            foreach (var summary in new[] { "read_file(a.cs)", "x", "", "一个很长的中文工具摘要内容" })
            {
                foreach (var isError in new[] { false, true })
                {
                    var line = AgentClass.FormatToolStatusLine(summary, isError, Elapsed, width);
                    Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
                }
            }
        }
    }

    [Fact]
    public void NarrowSummaryIsMarkedNotSilentlyCut()
    {
        // 摘要被丢弃时要明确标「…」，不能只是少显示几个字
        var line = AgentClass.FormatToolStatusLine("read_file(a.cs)", false, Elapsed, 12);
        Assert.Contains("…", line);
    }

    [Fact]
    public void ErrorAndSuccessUseTheSameBudget()
    {
        var ok = AgentClass.FormatToolStatusLine("read_file(a.cs)", false, Elapsed, 16);
        var bad = AgentClass.FormatToolStatusLine("read_file(a.cs)", true, Elapsed, 16);
        Assert.Equal(TextUtil.DisplayWidth(ok), TextUtil.DisplayWidth(bad));
    }

    [Fact]
    public void MinimumSummaryWidthIsSane()
    {
        Assert.InRange(AgentClass.MinToolSummaryWidth, 2, 8);
    }
}
