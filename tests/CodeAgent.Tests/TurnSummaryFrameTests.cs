using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 回合摘要行在窄终端下的降级。
/// 回归点：可丢段丢完后直接硬截，会得到
/// `── ✓ 完成 3 轮 5 次…` —— 一个「看起来是完整摘要」的残串，
/// 用户会以为工具次数/总耗时就只有那么多。而外框（`── … ──`）是纯装饰，
/// 白占 13 列却排在信息之后。
/// </summary>
public class TurnSummaryFrameTests
{
    private const string Elapsed = "1 分 23 秒";
    private const string TokenText = "↑ 8.1K ↓ 340";
    private const string ThinkText = "思考 12.5s";
    private const string CacheText = "42% cached";
    private const string CostText = "≈$0.13";

    private static string Build(int width, string token = TokenText, string think = ThinkText,
        string cache = CacheText, string cost = CostText) =>
        Program.BuildTurnSummary(3, 5, Elapsed, token, think, cache, cost, width);

    [Fact]
    public void WideTerminalKeepsFrameAndEverything()
    {
        var line = Build(200);
        Assert.StartsWith("── ✓ 完成 ", line);
        Assert.EndsWith(" ──", line);
        Assert.Contains(CostText, line);
    }

    [Fact]
    public void UnknownWidthKeepsEverythingUnchanged()
    {
        var line = Build(0);
        Assert.StartsWith("── ✓ 完成 ", line);
        Assert.Contains(CostText, line);
    }

    [Fact]
    public void NarrowTerminalDropsFrameBeforeTruncating()
    {
        // 脱框后核心段必须**完整**保留：轮数/工具调用/总耗时不能变成残串
        var line = Build(40);
        Assert.DoesNotContain("──", line);
        Assert.Contains("3 轮", line);
        Assert.Contains("5 次工具调用", line);
        Assert.Contains(Elapsed, line);
    }

    [Fact]
    public void FrameIsNeverTruncatedAway()
    {
        // 任何宽度下都不该出现「看起来完整、实际被切掉」的摘要
        for (var width = 16; width <= 120; width++)
        {
            var line = Build(width);
            if (TextUtil.DisplayWidth(line) <= width)
                continue;
            // 仍超宽（核心段本身就放不下）时必须带省略号标记截断
            Assert.EndsWith("…", line);
        }
    }

    [Fact]
    public void CoreSegmentsSurviveDownToTheirOwnWidth()
    {
        // 核心段（✓ + 3 轮 + 5 次工具调用 + 1 分 23 秒）= 脱框后的完整宽度
        var core = $"✓ 3 轮 5 次工具调用 {Elapsed}";
        var line = Build(TextUtil.DisplayWidth(core));
        Assert.Equal(core, line);
    }

    [Fact]
    public void OptionalSegmentsAreDroppedInPriorityOrder()
    {
        var line = Build(60);
        Assert.DoesNotContain(CostText, line); // 费用最先丢
    }

    [Fact]
    public void TokenDetailIsDroppedLastAmongOptionals()
    {
        // 恰好容下「外框 + 核心段 + token」：费用/缓存/思考都丢，token 保住
        // （预算必须按**带框**长度算，否则会误判成连 token 都放不下）
        var framed = $"── ✓ 完成 3 轮 5 次工具调用 {Elapsed} {TokenText} ──";
        var line = Build(TextUtil.DisplayWidth(framed));
        Assert.Contains("8.1K", line);
        Assert.DoesNotContain(CostText, line);
    }

    [Fact]
    public void NoDoubleSpacesWhenSegmentsAreDropped()
    {
        // 丢段后不能留下悬空空格（间隔忽宽忽窄会让摘要看起来破版）
        for (var width = 16; width <= 120; width++)
        {
            var line = Build(width);
            Assert.DoesNotContain("  ", line.Replace("── ✓", "──✓").Replace("──", ""));
        }
    }

    [Fact]
    public void FramelessLineStillLooksComplete()
    {
        // 脱框后必须仍以 ✓ 标识开头——不能变成一段没有上下文的裸文本
        var line = Build(40);
        Assert.StartsWith("✓ ", line);
    }
}
