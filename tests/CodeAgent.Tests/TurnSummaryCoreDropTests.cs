using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 回合摘要行的窄屏降级（核心段）。
/// 回归点：可丢段丢完、脱掉装饰外框之后，最后一步是 `FitToWidth` 硬截尾部——
/// 于是耗时被切成「12.…」。半个数字看起来像个**完整但不同**的值，
/// 比干脆不显示更容易误导（用户会以为回合只花了 12 毫秒）。
/// 现在核心段也按语义分级丢弃：工具次数 → 耗时 → 轮数，绝不产出半个数值。
/// </summary>
public class TurnSummaryCoreDropTests
{
    private const string Token = " token 1.2k";
    private const string Think = " 思考 2.5s";
    private const string Cache = " 60% cached";
    private const string Cost = " ≈$0.03";

    private static string Build(int width, int rounds = 3, int calls = 12, string elapsed = "12.3s") =>
        Program.BuildTurnSummary(rounds, calls, elapsed, Token, Think, Cache, Cost, width);

    [Fact]
    public void WideLineKeepsEverything()
    {
        var line = Build(120);
        Assert.Contains("3 轮", line);
        Assert.Contains("12 次工具调用", line);
        Assert.Contains("12.3s", line);
        Assert.Contains("cached", line);
        Assert.Contains("$0.03", line);
    }

    [Fact]
    public void NeverEmitsAHalfDuration()
    {
        // 关键不变式：任何宽度下都不出现「12.…」这种半个耗时
        foreach (var width in new[] { 4, 6, 8, 10, 12, 14, 16, 20, 24, 30, 40, 60, 80 })
        {
            var line = Build(width);
            Assert.DoesNotContain("12.…", line);
            Assert.DoesNotContain("1.…", line);
        }
    }

    [Fact]
    public void NeverEmitsAHalfCallCount()
    {
        foreach (var width in new[] { 6, 8, 10, 12, 16, 20, 30 })
            Assert.DoesNotContain("次工…", Build(width));
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var width in new[] { 4, 6, 8, 10, 12, 14, 16, 20, 24, 30, 40, 60, 80, 120 })
        {
            var line = Build(width);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width} 实际 {TextUtil.DisplayWidth(line)}：{line}");
        }
    }

    [Fact]
    public void NarrowestKeepsTheCompletionMark()
    {
        foreach (var width in new[] { 4, 6, 8, 10, 12 })
            Assert.StartsWith("✓", Build(width).TrimStart());
    }

    [Fact]
    public void DurationSurvivesWheneverItCanBeShownWhole()
    {
        // 「✓ 完成 3 轮 12.3s」= 15 列；放得下就必须显示完整耗时
        foreach (var width in new[] { 15, 16, 20, 30, 60 })
            Assert.Contains("12.3s", Build(width));
    }

    [Fact]
    public void VeryNarrowDropsTheDurationWhole()
    {
        // 「✓ 3 轮 12.3s」正好 12 列；更窄时必须整段去掉耗时，不留半个数字
        foreach (var width in new[] { 6, 7, 8, 9, 10, 11 })
        {
            var line = Build(width);
            Assert.DoesNotContain("s", line);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
        }
    }

    [Fact]
    public void UnknownWidthKeepsEverything()
    {
        var line = Build(0);
        Assert.Contains("3 轮", line);
        Assert.Contains("12 次工具调用", line);
        Assert.Contains("12.3s", line);
        Assert.Contains("── ✓ 完成", line);
    }

    [Fact]
    public void EmptyOptionalSegmentsAreOmitted()
    {
        // 可选段全空时，正文只有三个核心段，段间恰好一个空格（不留双空格）
        var line = Program.BuildTurnSummary(1, 2, "1.0s", "", "", "", "", 80);
        Assert.Equal("── ✓ 完成 1 轮 2 次工具调用 1.0s ──", line);
    }

    [Fact]
    public void FrameIsDroppedBeforeAnyNumber()
    {
        // 外框一次省 13 列，优先于丢工具次数/耗时
        var line = Build(30);
        Assert.DoesNotContain("──", line);
    }
}
