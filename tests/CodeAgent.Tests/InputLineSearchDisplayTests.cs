using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入行可见文本：历史浏览位置提示与 Ctrl+R 反向搜索的命中指示。
/// 回归点：旧实现未命中与命中的外观完全一致，用户无法判断搜索是否成功。
/// </summary>
public class InputLineSearchDisplayTests
{
    private const string Prompt = "[code|gpt-5] demo> ";

    [Fact]
    public void FormatInputText_PlainState_HasNoDecorations()
    {
        Assert.Equal(Prompt + "hello", InputLine.FormatInputText(Prompt, false, "", false, 0, 0, "hello"));
    }

    [Fact]
    public void FormatInputText_HistoryBrowsing_ShowsPosition()
    {
        Assert.Equal(
            Prompt + " (历史 2/5)cmd",
            InputLine.FormatInputText(Prompt, false, "", false, 1, 5, "cmd"));
    }

    [Fact]
    public void FormatInputText_HistoryAtBottomFallsBackToPlain()
    {
        // idx == session.Count 表示回到草稿态：不应再显示位置提示
        Assert.Equal(Prompt + "draft", InputLine.FormatInputText(Prompt, false, "", false, 5, 5, "draft"));
    }

    [Fact]
    public void FormatInputText_SearchHitAndMiss_AreDistinguishable()
    {
        var hit = InputLine.FormatInputText(Prompt, true, "build", true, 0, 0, "result");
        var miss = InputLine.FormatInputText(Prompt, true, "build", false, 0, 0, "result");
        Assert.Contains("(搜索)`build` ✔", hit);
        Assert.Contains("(搜索)`build` 未命中", miss);
        Assert.NotEqual(hit, miss);
    }

    [Fact]
    public void FormatInputText_EmptyQuery_HasNoVerdictMark()
    {
        var text = InputLine.FormatInputText(Prompt, true, "", false, 0, 0, "draft");
        Assert.Contains("(搜索)", text);
        Assert.DoesNotContain("未命中", text);
        Assert.DoesNotContain("✔", text);
    }

    [Fact]
    public void FormatInputText_LongQueryIsTruncatedToWidthBudget()
    {
        var text = InputLine.FormatInputText(Prompt, true, new string('a', 200), true, 0, 0, "d");
        var query = text[(text.IndexOf('`') + 1)..text.IndexOf('`', text.IndexOf('`') + 1)];
        Assert.True(TextUtil.DisplayWidth(query) <= InputLine.SearchQueryDisplayWidth);
        Assert.EndsWith("…", query, StringComparison.Ordinal);
        // 草稿仍可见：查询串不会把已命中内容挤出屏幕
        Assert.EndsWith(" d", text);
    }

    [Fact]
    public void FormatInputText_QueryCountsCjkAsTwoColumns()
    {
        var text = InputLine.FormatInputText(Prompt, true, new string('中', 50), true, 0, 0, "d");
        var start = text.IndexOf('`') + 1;
        var query = text[start..text.IndexOf('`', start)];
        Assert.True(TextUtil.DisplayWidth(query) <= InputLine.SearchQueryDisplayWidth);
    }
}
