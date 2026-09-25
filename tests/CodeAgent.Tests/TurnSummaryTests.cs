using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 回合摘要行的宽度自适应：按优先级丢段，保证窄终端下仍是一行。
/// 固定段保留「完成 + 轮数 + 工具调用 + 总耗时」，可丢段由低到高为费用/缓存/思考/token。
/// </summary>
public class TurnSummaryTests
{
    private const string Elapsed = "1.2s";
    private const string Tokens = "1,200 in / 340 out tok";
    private const string Think = " 思考 2.5s";
    private const string Cache = " 42% cached";
    private const string Cost = " ≈$0.0123";

    private static string Build(int width) =>
        Program.BuildTurnSummary(3, 5, Elapsed, Tokens, Think, Cache, Cost, width);

    [Fact]
    public void TurnSummary_Wide_KeepsEverySegment()
    {
        Assert.Equal(
            "── ✓ 完成 3 轮 5 次工具调用 1.2s 1,200 in / 340 out tok 思考 2.5s 42% cached ≈$0.0123 ──",
            Build(0));
        Assert.Equal(
            "── ✓ 完成 3 轮 5 次工具调用 1.2s 1,200 in / 340 out tok 思考 2.5s 42% cached ≈$0.0123 ──",
            Build(90));
        Assert.Equal(
            "── ✓ 完成 3 轮 5 次工具调用 1.2s 1,200 in / 340 out tok 思考 2.5s 42% cached ≈$0.0123 ──",
            Build(120));
    }

    [Fact]
    public void TurnSummary_NarrowDropsCostFirst()
    {
        // 完整行 90 列：85 列时只丢费用（90-9=81 放得下）
        var text = Build(85);
        Assert.DoesNotContain("$", text);
        Assert.Contains("42% cached", text);
        Assert.Contains("思考 2.5s", text);
    }

    [Fact]
    public void TurnSummary_NarrowerDropsCacheThenThink()
    {
        // 70 列：费用 + 缓存都放不下（81 > 70），丢掉后 69 列
        var at70 = Build(70);
        Assert.DoesNotContain("cached", at70);
        Assert.DoesNotContain("$", at70);
        Assert.Contains("思考 2.5s", at70);
        // 60 列：思考段也放不下（69 > 60），丢掉后 59 列
        var at60 = Build(60);
        Assert.DoesNotContain("思考", at60);
        Assert.Contains("1,200 in / 340 out tok", at60);
    }

    [Fact]
    public void TurnSummary_NarrowestKeepsCoreConclusion()
    {
        var text = Build(45);
        Assert.DoesNotContain("out tok", text);
        Assert.Contains("完成 3 轮 5 次工具调用 1.2s", text);
    }

    [Fact]
    public void TurnSummary_EmptyOptionalSegmentsAreNotRendered()
    {
        Assert.Equal(
            "── ✓ 完成 1 轮 0 次工具调用 0.3s 10 in / 5 out tok ──",
            Program.BuildTurnSummary(1, 0, "0.3s", "10 in / 5 out tok", "", "", "", 0));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(110)]
    public void TurnSummary_NeverExceedsTerminalWidth(int width)
    {
        Assert.True(TextUtil.DisplayWidth(Build(width)) <= width, $"宽度 {width} 溢出");
    }
}
