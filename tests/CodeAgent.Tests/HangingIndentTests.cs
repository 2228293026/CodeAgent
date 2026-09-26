using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 对话正文中列表项 / 块引用的悬挂缩进。
/// 回归点：此前正文直接原样输出，完全依赖终端软换行——
/// 续行顶到行首后视觉上变成新段落，读者看不出「这一行仍属于上一条列表项」。
/// </summary>
public class HangingIndentTests
{
    [Fact]
    public void HangingIndent_BulletItem()
    {
        Assert.Equal("- ", ConsoleRenderer.HangingIndentFor("- 某条很长的说明文字"));
        Assert.Equal("  - ", ConsoleRenderer.HangingIndentFor("  - 缩进的列表项"));
    }

    [Fact]
    public void HangingIndent_OrderedItem()
    {
        Assert.Equal("1. ", ConsoleRenderer.HangingIndentFor("1. 第一项"));
        Assert.Equal("10. ", ConsoleRenderer.HangingIndentFor("10. 第十项"));
    }

    [Fact]
    public void HangingIndent_Blockquote()
    {
        Assert.Equal("> ", ConsoleRenderer.HangingIndentFor("> 引用内容"));
        Assert.Equal("> > ", ConsoleRenderer.HangingIndentFor("> > 两层引用"));
    }

    [Fact]
    public void HangingIndent_PlainLineHasNone()
    {
        Assert.Equal("", ConsoleRenderer.HangingIndentFor("普通段落，不该有悬挂缩进"));
        Assert.Equal("", ConsoleRenderer.HangingIndentFor("# 标题"));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    public void WrappedItemKeepsHangingIndent(int width)
    {
        var hanging = "- ";
        var text = hanging + string.Join(" ", Enumerable.Repeat("这是一段需要换行的中文说明", 6));
        // WrapDisplay 的 width 是正文预算，indent 额外加在每行前面 → 必须扣掉
        var wrapped = TextUtil.WrapDisplay(text[hanging.Length..], width - hanging.Length, hanging);
        var lines = wrapped.Split('\n');
        Assert.True(lines.Length > 1, "应当发生换行");
        foreach (var line in lines)
        {
            Assert.StartsWith(hanging, line); // 续行才不会看起来像新段落
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
        }
        Assert.DoesNotContain("- - ", wrapped); // 标记不得重复
    }

    [Fact]
    public void WrappedBlockquoteKeepsGutter()
    {
        const string hanging = "> ";
        var text = hanging + string.Join(" ", Enumerable.Repeat("引用一段很长的中文内容", 6));
        var wrapped = TextUtil.WrapDisplay(text[hanging.Length..], 40 - hanging.Length, hanging);
        foreach (var line in wrapped.Split('\n'))
        {
            Assert.StartsWith(hanging, line);
            Assert.True(TextUtil.DisplayWidth(line) <= 40, $"溢出: {line}");
        }
        Assert.DoesNotContain("> > ", wrapped);
    }

    [Fact]
    public void ShortItemIsNotWrapped()
    {
        const string hanging = "- ";
        const string body = "短条目";
        var wrapped = TextUtil.WrapDisplay(body, 60, hanging);
        Assert.Equal(hanging + body, wrapped);
    }
}
