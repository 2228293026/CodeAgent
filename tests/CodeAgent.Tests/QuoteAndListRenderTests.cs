using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 块引用与列表标记渲染：识别遵循 CommonMark，标记统一规范化。
/// 回归点：旧实现只看首字符 `>`，`  > 内容`（合法引用）不上色；
/// `>内容`、`1)item`、`+   item` 的标记与间距各写各的，同一列表里参差不齐。
/// </summary>
[Collection("ConsoleOutput")]
public class QuoteAndListRenderTests
{
    [Theory]
    [InlineData("> 内容", 1)]
    [InlineData(">内容", 1)]          // 无空格也是引用
    [InlineData("  > 内容", 1)]       // 2 个前导空格仍合法
    [InlineData(">>> 深层", 3)]
    [InlineData("> > 两层", 2)]
    [InlineData("    > 内容", 0)]     // 4 个空格是代码块
    [InlineData("普通文本", 0)]
    [InlineData(">引用", 1)]
    public void BlockquoteLevel_CountsMarkers(string line, int expected) =>
        Assert.Equal(expected, ConsoleRenderer.BlockquoteLevel(line));

    [Theory]
    [InlineData("> 内容", "> 内容")]
    [InlineData(">内容", "> 内容")]
    [InlineData("  > 内容", "> 内容")]
    [InlineData(">>>深层", "> > > 深层")]
    [InlineData("> > 两层", "> > 两层")]
    [InlineData("普通", "普通")]
    public void NormalizeBlockquote_UnifiesMarkerAndSpacing(string line, string expected) =>
        Assert.Equal(expected, ConsoleRenderer.NormalizeBlockquote(line));

    [Theory]
    [InlineData("- 项", "- 项")]
    [InlineData("* 项", "- 项")]
    [InlineData("+ 项", "- 项")]
    [InlineData("1. 项", "1. 项")]
    [InlineData("1) 项", "1. 项")]
    [InlineData("-   多空格", "- 多空格")]
    [InlineData("   * 缩进项", "   - 缩进项")]
    public void NormalizeListMarker_UnifiesMarkerAndSpacing(string line, string expected) =>
        Assert.Equal(expected, ConsoleRenderer.NormalizeListMarker(line));

    [Theory]
    [InlineData("普通文本")]
    [InlineData("-5 是负数")]        // 标记后无空格，不是列表项
    [InlineData("1.5 版本号")]
    [InlineData("1)项")]             // CommonMark 要求有序标记后有空格
    [InlineData("    - 代码缩进")]   // 4 个空格是代码块
    [InlineData("")]
    public void NormalizeListMarker_LeavesNonListLines(string line) =>
        Assert.Null(ConsoleRenderer.NormalizeListMarker(line));

    [Fact]
    public void EmitLine_NormalizesBlockquoteAndList()
    {
        var output = Render(">引用\n* 第一项\n2) 第二项\n");
        Assert.Contains("> 引用", output);
        Assert.Contains("- 第一项", output);
        Assert.Contains("2. 第二项", output);
    }

    private static string Render(string text)
    {
        var original = Console.Out;
        var writer = new System.IO.StringWriter();
        Console.SetOut(writer);
        try
        {
            var renderer = new ConsoleRenderer(enabled: true);
            renderer.Append(text);
            renderer.Flush();
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }
}
