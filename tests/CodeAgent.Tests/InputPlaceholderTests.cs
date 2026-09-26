using System;
using Xunit;

namespace CodeAgent.Tests;

[Collection("ConsoleOutput")]
public sealed class InputPlaceholderTests
{
    private const string Prompt = "[code|model] CodeAgent> ";

    [Fact]
    public void Placeholder_ShowsWhenInputEmptyAndWindowIsWide()
    {
        var text = InputLine.BuildPlaceholder(InputLine.DefaultPlaceholder, 96, TextUtil.DisplayWidth(Prompt), colored: false);
        Assert.Equal(InputLine.DefaultPlaceholder, text);
    }

    [Fact]
    public void Placeholder_NeverExceedsTheBudget()
    {
        // 硬约束：占位提示折行会把输入块撑成两行，光标定位与多行重绘全部错位
        foreach (var budget in new[] { 20, 30, 40, 55, 70, 96 })
        {
            var text = InputLine.BuildPlaceholder(InputLine.DefaultPlaceholder, budget, TextUtil.DisplayWidth(Prompt), colored: false);
            Assert.True(TextUtil.DisplayWidth(text) <= Math.Max(0, budget - TextUtil.DisplayWidth(Prompt)),
                $"budget={budget} 渲染宽 {TextUtil.DisplayWidth(text)} 超出");
        }
    }

    [Fact]
    public void Placeholder_DisappearsEntirelyWhenItCannotFit()
    {
        // 只剩 3~5 列时显示「试试 …」既没信息又挤用户输入的内容
        var room = 5;
        var text = InputLine.BuildPlaceholder(InputLine.DefaultPlaceholder, room + TextUtil.DisplayWidth(Prompt), TextUtil.DisplayWidth(Prompt), colored: false);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Placeholder_UnknownWidthShowsNothing()
    {
        // 宽度未知时不猜：猜错会让窄终端多出一行
        Assert.Equal(string.Empty, InputLine.BuildPlaceholder(InputLine.DefaultPlaceholder, 0, 10, colored: false));
    }

    [Fact]
    public void Placeholder_TruncatedFormCarriesEllipsis()
    {
        // 截断的提示读起来像"还有更多"，这是可接受的；数值被截断才是不���接受的误导
        var text = InputLine.BuildPlaceholder(InputLine.DefaultPlaceholder, 40, TextUtil.DisplayWidth(Prompt), colored: false);
        Assert.NotEqual(string.Empty, text);
        Assert.NotEqual(InputLine.DefaultPlaceholder, text);
        Assert.Contains(SafeColor.Glyphs.Ellipsis, text);
    }

    [Fact]
    public void Placeholder_EmptyTextYieldsNothing()
    {
        Assert.Equal(string.Empty, InputLine.BuildPlaceholder(null, 96, 10, colored: false));
        Assert.Equal(string.Empty, InputLine.BuildPlaceholder(string.Empty, 96, 10, colored: false));
    }

    [Fact]
    public void Placeholder_CanBeDisabled()
    {
        // /clear 之后或某些场景不需要提示，显式传 null 即可关掉
        Assert.Equal(string.Empty, InputLine.BuildPlaceholder(null, 96, 10, colored: false));
    }

    [Fact]
    public void ShouldShow_OnlyInPlainEmptyState()
    {
        // 普通态 + 空输入
        Assert.True(InputLine.ShouldShowPlaceholder("", searching: false, historyIndex: -1, historyCount: 0));
        Assert.True(InputLine.ShouldShowPlaceholder("", searching: false, historyIndex: 0, historyCount: 0));
        // 一旦有输入（哪怕只有一个空格）立刻消失，否则残留的提示会被当成用户输入
        Assert.False(InputLine.ShouldShowPlaceholder(" ", searching: false, historyIndex: -1, historyCount: 0));
        Assert.False(InputLine.ShouldShowPlaceholder("x", searching: false, historyIndex: -1, historyCount: 0));
        // 搜索态和浏览历史态下输入框有别的含义，挂提示只会误导
        Assert.False(InputLine.ShouldShowPlaceholder("", searching: true, historyIndex: -1, historyCount: 0));
        Assert.False(InputLine.ShouldShowPlaceholder("", searching: false, historyIndex: 2, historyCount: 5));
    }

    [Fact]
    public void FormatInput_AppendsPlaceholderOnlyWhenEmpty()
    {
        var empty = InputLine.FormatInputText(Prompt, false, "", false, -1, 0, "", 24, 96,
            InputLine.DefaultPlaceholder, colored: false);
        Assert.Equal(Prompt + InputLine.DefaultPlaceholder, empty);

        var typed = InputLine.FormatInputText(Prompt, false, "", false, -1, 0, "/help", 24, 96,
            InputLine.DefaultPlaceholder, colored: false);
        Assert.Equal(Prompt + "/help", typed);
    }

    [Fact]
    public void FormatInput_NoPlaceholderWhenNoneConfigured()
    {
        var text = InputLine.FormatInputText(Prompt, false, "", false, -1, 0, "", 24, 96, null, colored: false);
        Assert.Equal(Prompt, text);
    }

    [Fact]
    public void FormatInput_SearchStateWinsOverPlaceholder()
    {
        var text = InputLine.FormatInputText(Prompt, true, "ab", true, -1, 0, "", 24, 96,
            InputLine.DefaultPlaceholder, colored: false);
        Assert.DoesNotContain(InputLine.DefaultPlaceholder, text);
    }

    [Fact]
    public void DefaultPlaceholder_MentionsBothEntryStyles()
    {
        // 提示要同时给出「问代码」和「下任务」两种用法，否则只教一种
        Assert.Contains("<filepath>", InputLine.DefaultPlaceholder);
        Assert.Contains("任务", InputLine.DefaultPlaceholder);
    }

    [Fact]
    public void Placeholder_UncoloredHasNoAnsiEscapes()
    {
        var text = InputLine.BuildPlaceholder(InputLine.DefaultPlaceholder, 96, 10, colored: false);
        Assert.DoesNotContain('\x1b', text);
    }
}
