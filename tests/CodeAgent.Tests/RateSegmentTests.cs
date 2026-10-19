using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 状态栏的速率段：直方图 + tps 读数。
///
/// 参考样式里状态栏挂着 <c>▁▂▄▁▂▂█▆▆▅▃▅</c>——一行里看出「刚才快、现在慢」。
/// 这类装饰最容易写歪的地方有两个：一是**没有数据也画一条平线**（读起来像速率恒为 0），
/// 二是**窄终端上截成半截**（半截图看不出趋势，反而比没有更误导）。
/// </summary>
/// <remarks>
/// 在 <c>ConsoleOutput</c> 集合里：字形取自 <c>SafeColor.Glyphs</c>，
/// 它读的是进程级可替换的 <c>SafeColor.ReadEnv</c>。
/// </remarks>
[Collection("ConsoleOutput")]
public class RateSegmentTests
{
    [Fact]
    public void RisingRatesProduceRisingBars()
    {
        // 0..7 均匀铺开：每一级都应落在对应的档上
        var bars = new[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var text = Program.FormatRateSegment(bars, 42.0);
        var levels = SafeColor.Glyphs.SparkLevels;
        var expected = string.Concat(bars.Select(b => levels[b]));
        Assert.StartsWith(expected, text, StringComparison.Ordinal);
        Assert.EndsWith("42.0 t/s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RateIsShownWithOneDecimal()
    {
        // 整数位在直方图旁边已够读，多位小数只是噪音
        Assert.Contains("7.5 t/s", Program.FormatRateSegment([3], 7.5));
        Assert.Contains("0.0 t/s", Program.FormatRateSegment([0], 0));
    }

    [Fact]
    public void NoSamplesMeansNoSegmentAtAll()
    {
        // 没有样本时不占位：画一条空图会让人以为速率是 0
        Assert.Equal(string.Empty, Program.FormatRateSegment([], 0));
        Assert.Equal(string.Empty, Program.FormatRateSegment([], 99.9));
    }

    [Fact]
    public void NarrowWidthDropsTheNumberBeforeTheSpark()
    {
        // 先砍数值（趋势还在），再砍图（整段不显示）——绝不截成半截
        var text = Program.FormatRateSegment([1, 2, 3], 42.0, 3);
        Assert.Equal(3, TextUtil.DisplayWidth(text));
        Assert.DoesNotContain("t/s", text);
    }

    [Fact]
    public void TooNarrowForEvenTheSparkDropsEverything()
    {
        Assert.Equal(string.Empty, Program.FormatRateSegment([1, 2, 3], 42.0, 2));
    }

    [Fact]
    public void SegmentNeverExceedsTheGivenWidth()
    {
        // 1..80 全扫：越界一次就够把状态栏顶到折行。
        // 宽度 0 单独说清楚——那是「未知」，语义是**不截断**而不是「宽度为零」，
        // 与 BuildStatusBar/InputLine 里 width<=0 的含义一致。
        for (var w = 1; w <= 80; w++)
        {
            var text = Program.FormatRateSegment([0, 3, 7, 1, 5], 123.4, w);
            Assert.True(TextUtil.DisplayWidth(text) <= w, $"宽度 {w}：速率段 {TextUtil.DisplayWidth(text)} 列越界 → [{text}]");
        }
        // 宽度未知：原样给出全部信息，由调用方的整行预算决定要不要
        Assert.Contains("123.4 t/s", Program.FormatRateSegment([0, 3, 7, 1, 5], 123.4, 0));
    }

    [Fact]
    public void AsciiFallbackUsesReadableLevels()
    {
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = n => n == "CODEAGENT_ASCII" ? "1" : null;
            var text = Program.FormatRateSegment([0, 7], 10.0);
            Assert.DoesNotContain("▁", text);
            Assert.DoesNotContain("█", text);
            Assert.StartsWith(".@", text, StringComparison.Ordinal);
        }
        finally { SafeColor.ReadEnv = saved; }
    }

    [Fact]
    public void StatusBarCarriesTheRateSegmentWhenThereIsRoom()
    {
        var bar = Program.BuildStatusBar(
            "code", "m", "D:/P", "main", "0", "0", "ctx 1%", "think:high", 200,
            rate: "▁▂▄ 42.0 t/s");
        Assert.Contains("42.0 t/s", bar);
    }

    [Fact]
    public void RateSegmentIsTheFirstThingDroppedWhenNarrow()
    {
        // 它是唯一一段**只描述过去**的信息，空间不够时第一个走；
        // 而 ctx（离上限还有多远）必须活到最后。
        var bar = Program.BuildStatusBar(
            "code", "m", "D:/P", "main", "1.2K", "340", "ctx 12%", "think:high", 40,
            rate: "▁▂▄▁▂▂█▆▆▅▃▅ 42.0 t/s");
        Assert.DoesNotContain("t/s", bar);
        Assert.Contains("ctx 12%", bar);
    }
}
