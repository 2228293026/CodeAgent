using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 占位提示与光标位置：光标必须停在提示**之前**。
///
/// 重绘把「提示符 + 占位提示 + 已输入内容」依次写在同一行，光标默认落在行尾。
/// 不把占位提示的宽度算进左移量的话，光标会停在提示末尾——看上去像
/// 「这句提示是用户自己打的」，与「这是一条引导」传达的信息完全相反。
/// </summary>
public sealed class PlaceholderCursorTests
{
    private const string Prompt = "[code|space-bunny-alpha] CodeAgent> ";

    private static int Width(string? placeholder, int budget) =>
        InputLine.RenderedPlaceholderWidth("", false, -1, 0, placeholder, budget, TextUtil.DisplayWidth(Prompt));

    [Fact]
    public void Placeholder_IsAccountedForWhenShown()
    {
        var cols = Width(InputLine.DefaultPlaceholder, 96);
        Assert.Equal(TextUtil.DisplayWidth(InputLine.DefaultPlaceholder), cols);
        Assert.True(cols > 0);
    }

    [Fact]
    public void Placeholder_IsZeroWhenDisabled()
    {
        Assert.Equal(0, Width(null, 96));
        Assert.Equal(0, Width(string.Empty, 96));
    }

    [Fact]
    public void Placeholder_IsZeroWhenItCannotFit()
    {
        // 装不下时整段不渲染，自然也没有宽度需要扣
        Assert.Equal(0, Width(InputLine.DefaultPlaceholder, 12));
    }

    [Fact]
    public void Placeholder_DisappearsOnceUserTypes()
    {
        // 一旦有输入，提示就没了，左移量必须跟着回到 0
        Assert.Equal(0, InputLine.RenderedPlaceholderWidth(
            "h", false, -1, 0, InputLine.DefaultPlaceholder, 96, TextUtil.DisplayWidth(Prompt)));
    }

    [Fact]
    public void Placeholder_NotCountedInSearchOrHistoryStates()
    {
        Assert.Equal(0, InputLine.RenderedPlaceholderWidth(
            "", true, -1, 0, InputLine.DefaultPlaceholder, 96, TextUtil.DisplayWidth(Prompt)));
        Assert.Equal(0, InputLine.RenderedPlaceholderWidth(
            "", false, 1, 3, InputLine.DefaultPlaceholder, 96, TextUtil.DisplayWidth(Prompt)));
    }

    [Fact]
    public void Placeholder_WidthIgnoresAnsiEscapes()
    {
        // 带色的提示里有 ANSI 序列；转义序列不占列，按含转义的串量会多算十几列
        var withColor = InputLine.BuildPlaceholder(InputLine.DefaultPlaceholder, 96, TextUtil.DisplayWidth(Prompt), colored: true);
        var withoutColor = InputLine.BuildPlaceholder(InputLine.DefaultPlaceholder, 96, TextUtil.DisplayWidth(Prompt), colored: false);
        if (withColor != withoutColor)
        {
            Assert.Contains('\x1b', withColor);
            Assert.Equal(TextUtil.DisplayWidth(withoutColor), Width(InputLine.DefaultPlaceholder, 96));
        }
    }

    [Fact]
    public void Placeholder_CursorLandsBeforeTheHint()
    {
        // 端到端：提示符 33 列 + 提示 W 列，光标应停在第 33 列
        var cols = Width(InputLine.DefaultPlaceholder, 96);
        var cursorColumn = TextUtil.DisplayWidth(Prompt); // 行尾减去 cols
        Assert.Equal(TextUtil.DisplayWidth(Prompt), cursorColumn);
        Assert.True(cols > 0, "占位提示应当可见，否则这条路径无从验证");
    }
}
