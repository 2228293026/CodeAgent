using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 历史浏览态输入行的宽度安全。
/// 回归点：`(历史 N/M)` 随历史条数变长（上千条时约 15 列），而草稿此前**完全没有预算**，
/// 窄终端上会被整段挤出屏幕——用户以为自己的输入被清空了。
/// 注意与搜索行的优先级相反：标记是导航上下文（丢了没法恢复），草稿是内容（丢了能重打）。
/// </summary>
public class HistoryRowWidthTests
{
    private const string Prompt = "› ";

    [Fact]
    public void UnknownWidthConcatenatesUnchanged()
    {
        var line = InputLine.FormatHistoryRow(Prompt, 2, 7, "草稿", 0);
        Assert.Equal($"{Prompt} (历史 3/7)草稿", line);
    }

    [Fact]
    public void WideTerminalKeepsTheWholeDraft()
    {
        var line = InputLine.FormatHistoryRow(Prompt, 2, 7, "完整的草稿内容", 200);
        Assert.Equal($"{Prompt} (历史 3/7)完整的草稿内容", line);
    }

    [Fact]
    public void MarkerIsAlwaysKept()
    {
        // 关键保证：无论多窄，都能看到自己在第几条、共几条（超宽时退化为紧凑形式，位置信息不丢）
        foreach (var width in new[] { 8, 12, 16, 20, 40 })
        {
            var line = InputLine.FormatHistoryRow(Prompt, 998, 1500, "草稿", width);
            Assert.Contains("999/1500", line);
        }
    }

    [Fact]
    public void TruncatedDraftIsAlwaysMarked()
    {
        // 草稿放不下时必须出现「…」，不能无声消失。
        // 从 16 列起紧凑标记（13 列）还留得下省略号；8/12 列属于「标记本身就超宽」的极端情形。
        foreach (var width in new[] { 16, 20 })
        {
            var line = InputLine.FormatHistoryRow(Prompt, 998, 1500, "草稿内容较长", width);
            Assert.Contains("…", line);
        }
    }

    [Fact]
    public void NarrowTerminalTruncatesTheDraftWithAnEllipsis()
    {
        var line = InputLine.FormatHistoryRow(Prompt, 2, 7, "这是一段很长的草稿内容", 24);
        Assert.Contains("(历史 3/7)", line);
        Assert.EndsWith("…", line);
        Assert.True(TextUtil.DisplayWidth(line) <= 24, $"实际 {TextUtil.DisplayWidth(line)}");
    }

    [Fact]
    public void NoRoomForDraftStillMarksTruncation()
    {
        // 放不下任何有意义的草稿时必须留「…」，不能留空——空行看起来像草稿不存在
        var line = InputLine.FormatHistoryRow(Prompt, 2, 7, "草稿", 10);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void NeverExceedsTheWindowOnceTheCompactMarkerFits()
    {
        // 紧凑标记自身就比窗口宽时（999/1500 至少 11 列），位置信息优先于宽度：
        // 这时数字本身就要超宽，契约只覆盖「标记放得下」的情形。
        foreach (var width in new[] { 16, 20, 24, 32, 48, 80 })
        {
            var line = InputLine.FormatHistoryRow(Prompt, 998, 1500, "一段比较长的草稿内容用于测试", width);
            Assert.True(TextUtil.DisplayWidth(line) <= width,
                $"窗口 {width} 列时实际 {TextUtil.DisplayWidth(line)}：{line}");
        }
    }

    [Fact]
    public void ExtremeNarrownessKeepsTheNumbersEvenIfWide()
    {
        // 极窄时宁可超宽也要保住位置信息——数字被截掉就成了误导
        var line = InputLine.FormatHistoryRow(Prompt, 998, 1500, "草稿", 8);
        Assert.Contains("999/1500", line);
    }

    [Fact]
    public void FormatInputTextRoutesHistoryThroughTheSameRule()
    {
        var line = InputLine.FormatInputText(Prompt, false, "", false, 2, 7, "草稿内容",
            InputLine.SearchQueryDisplayWidth, windowWidth: 24);
        Assert.Equal(InputLine.FormatHistoryRow(Prompt, 2, 7, "草稿内容", 24), line);
    }

    [Fact]
    public void PlainModeStaysUnaffected()
    {
        // 非历史态不套用标记，也就不该截断——用户正在正常编辑
        // （historyCount = 0 表示没有历史可浏览，index 落在范围外）
        Assert.Equal(Prompt + "完整草稿",
            InputLine.FormatInputText(Prompt, false, "", false, 0, 0, "完整草稿",
                InputLine.SearchQueryDisplayWidth, windowWidth: 20));
    }

    [Fact]
    public void SearchModeStillPrefersTheDraft()
    {
        // 对照：搜索态是「草稿优先」，历史态是「标记优先」——两者不可混用
        var line = InputLine.FormatInputText(Prompt, true, "查询", true, 0, 0, "草稿",
            InputLine.SearchQueryDisplayWidth, windowWidth: 10);
        Assert.Contains("草稿", line);
    }

    [Fact]
    public void MinHistoryDraftWidthIsUsable()
    {
        Assert.True(InputLine.MinHistoryDraftWidth >= 4, "少于 4 列的草稿没有信息量");
    }
}
