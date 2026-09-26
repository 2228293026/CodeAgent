using System;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具状态行的工具名缩短。
/// 回归点：一个参数都放不下时，旧实现直接对工具名做显示宽度硬截，
/// 得到 `read_fi…` —— 那看起来像一个真实存在的工具名，用户照着去 /help 里找却根本找不到。
/// 正确做法是按**下划线边界**缩短成 `read_…`，保留「这是个被缩过的名字」的可辨识形态。
/// </summary>
public class ToolNameShortenTests
{
    [Fact]
    public void FitsNameIsUnchanged()
    {
        Assert.Equal("read_file", AgentClass.ShortenToolName("read_file", 20));
    }

    [Fact]
    public void ShortensAtUnderscoreBoundary()
    {
        // 只保留放得下的**完整下划线段**：`read_file_with_tail` → `read…`（不产出 read_fi 这种残串）
        var shortened = AgentClass.ShortenToolName("read_file_with_tail", 8);
        Assert.Equal("read…", shortened);
    }

    [Fact]
    public void NeverProducesAMidIdentifierFragment()
    {
        // 关键保证：缩短结果必须是**原名按段截断**，不能是按字符硬切
        foreach (var budget in new[] { 4, 5, 6, 7, 8, 9, 10, 12 })
        {
            var shortened = AgentClass.ShortenToolName("read_file", budget);
            var withoutEllipsis = shortened.TrimEnd('…');
            Assert.True(
                "read_file".StartsWith(withoutEllipsis, StringComparison.Ordinal),
                $"预算 {budget} 得到 {shortened}，不是原名的段前缀");
        }
    }

    [Fact]
    public void SingleSegmentNameFallsBackToHardTruncate()
    {
        // 没有下划线可切（ls / pwd 之类）只能硬截
        var shortened = AgentClass.ShortenToolName("abcdefgh", 4);
        Assert.Equal("abc…", shortened);
    }

    [Fact]
    public void ZeroBudgetReturnsEmpty()
    {
        Assert.Equal("", AgentClass.ShortenToolName("read_file", 0));
        Assert.Equal("", AgentClass.ShortenToolName("read_file", -1));
    }

    [Fact]
    public void CjkNameIsShortenedBySegmentToo()
    {
        var shortened = AgentClass.ShortenToolName("读取_文件_内容", 6);
        Assert.Contains("…", shortened);
        Assert.StartsWith("读取", shortened);
    }

    [Fact]
    public void SummaryWithNoArgsIsShortenedNotFragmented()
    {
        var summary = AgentClass.TrimToolSummaryArgs("read_file_with_long_name(path=x)", 8);
        Assert.DoesNotContain("path=x", summary);
        Assert.Contains("…", summary);
    }

    [Fact]
    public void SummaryKeepsTheArgumentPlaceholder()
    {
        // 丢掉参数列表时要留 (…)，让读者知道「这里本来有参数」。
        // 预算 9 = 缩短后的名字 `read…`(5) + `(…)`(3)，还有余量。
        var summary = AgentClass.TrimToolSummaryArgs("read_file_tool(path=very/long/path)", 9);
        Assert.Contains("(…)", summary);
        Assert.DoesNotContain("path=very", summary);
    }

    [Fact]
    public void StatusLineNeverExceedsTheWidth()
    {
        var elapsed = TimeSpan.FromSeconds(1.25);
        foreach (var width in new[] { 20, 24, 30, 40, 60, 80 })
        {
            var line = AgentClass.FormatToolStatusLine("read_file_with_long_name(path=x)", false, elapsed, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width,
                $"预算 {width} 实际 {TextUtil.DisplayWidth(line)}：{line}");
        }
    }

    [Fact]
    public void StatusLineKeepsTheMarkOnNarrowTerminals()
    {
        // ✔/⚠ 是行首最强的视觉锚点，不能被裁掉
        foreach (var width in new[] { 12, 16, 20, 30 })
        {
            Assert.StartsWith("  ✔", AgentClass.FormatToolStatusLine("read_file(path=x)", false, TimeSpan.Zero, width));
        }
    }
}
