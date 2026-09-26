using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// ATX 标题识别（CommonMark）与闭合序列剥离。
/// 回归点：旧实现只看首字符是否 '#'——`#Title`（无空格）被当标题上色，
/// 而合法的 `   # 标题`（前导空格）却不上色；闭合序列 `## 标题 ##` 原样漏出到渲染结果。
/// </summary>
[Collection("ConsoleOutput")]
public class HeadingRenderTests
{
    [Theory]
    [InlineData("# 标题", 1)]
    [InlineData("## 标题", 2)]
    [InlineData("###### 六级", 6)]
    [InlineData("####### 七级不是标题", 0)]
    [InlineData("#", 1)]                 // 空的合法标题
    [InlineData("   # 标题", 1)]          // 3 个前导空格仍合法
    [InlineData("    # 标题", 0)]         // 4 个空格是代码块
    [InlineData("#Title", 0)]             // 井号后无空格：不是标题
    [InlineData("###foo", 0)]
    [InlineData("普通文本", 0)]
    [InlineData("", 0)]
    [InlineData(" #", 1)]
    public void HeadingLevel_FollowsCommonMark(string line, int expected) =>
        Assert.Equal(expected, ConsoleRenderer.HeadingLevel(line));

    [Theory]
    [InlineData("## 标题 ##", "## 标题")]
    [InlineData("# 标题 ###", "# 标题")]
    [InlineData("### 标题", "### 标题")]     // 无闭合序列
    [InlineData("## a#b#", "## a#b#")]       // 闭合标记前无空白，不算闭合
    [InlineData("#", "#")]
    [InlineData("# #", "#")]
    public void StripClosingHashes_RemovesOnlyRealClosingSequence(string line, string expected) =>
        Assert.Equal(expected, ConsoleRenderer.StripClosingHashes(line));

    [Fact]
    public void EmitLine_RendersHeadingWithoutClosingSequence()
    {
        var output = Render("## 标题 ##\n");
        Assert.Contains("## 标题", output);
        Assert.DoesNotContain("## 标题 ##", output);
    }

    [Fact]
    public void EmitLine_KeepsHashPrefixedNonHeading()
    {
        // #Title 不是标题，井号必须原样保留
        var output = Render("#Title 不是标题\n");
        Assert.Contains("#Title 不是标题", output);
    }

    [Fact]
    public void EmitLine_RendersIndentedHeading()
    {
        var output = Render("   # 缩进标题\n");
        Assert.Contains("# 缩进标题", output);
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
