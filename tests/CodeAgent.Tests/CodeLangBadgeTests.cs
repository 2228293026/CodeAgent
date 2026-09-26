using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 代码块的围栏语言徽标。
/// 回归点：围栏语言标注（```cs 的 cs）此前被**直接丢弃**——读者完全无从判断
/// 下面这段是什么语言的代码，只能靠猜。而徽标本身也可能放不下：
/// 窄屏下让一行徽标自己折行，会把整个代码块顶歪，所以放不下时必须整行省略。
/// </summary>
public class CodeLangBadgeTests
{
    private static string Capture(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { action(); }
        finally { Console.SetOut(original); }
        return writer.ToString();
    }

    [Fact]
    public void BadgeShowsTheLanguage()
    {
        Assert.Equal("  ▌cs", ConsoleRenderer.FormatCodeLangBadge("cs", 80));
    }

    [Fact]
    public void EmptyLanguageProducesNoBadge()
    {
        Assert.Equal("", ConsoleRenderer.FormatCodeLangBadge("", 80));
        Assert.Equal("", ConsoleRenderer.FormatCodeLangBadge("   ", 80));
    }

    [Fact]
    public void OnlyTheFirstTokenIsUsed()
    {
        // `cs extra` 不是语言名：徽标不该变成一整行废话
        Assert.Equal("  ▌cs", ConsoleRenderer.FormatCodeLangBadge("cs extra stuff", 80));
    }

    [Fact]
    public void TooNarrowProducesNoBadge()
    {
        // 宁可不标，也不能让徽标自己折行把代码块顶歪
        Assert.Equal("", ConsoleRenderer.FormatCodeLangBadge("csharp", 3));
    }

    [Fact]
    public void NeverExceedsTheWidthWhenItFits()
    {
        foreach (var width in new[] { 4, 5, 6, 8, 10, 20, 80 })
        {
            var badge = ConsoleRenderer.FormatCodeLangBadge("csharp", width);
            Assert.True(TextUtil.DisplayWidth(badge) <= width, $"预算 {width}：{badge}");
        }
    }

    [Fact]
    public void VeryLongLanguageIsBounded()
    {
        var badge = ConsoleRenderer.FormatCodeLangBadge(new string('a', 200), 200);
        Assert.True(TextUtil.DisplayWidth(badge) <= 30, $"徽标 {TextUtil.DisplayWidth(badge)} 列");
    }

    [Fact]
    public void UnknownWidthKeepsTheBadge()
    {
        Assert.Equal("  ▌cs", ConsoleRenderer.FormatCodeLangBadge("cs", 0));
    }

    [Fact]
    public void RendererPrintsTheBadgeBeforeTheCode()
    {
        var renderer = new ConsoleRenderer(true);
        var output = Capture(() => renderer.Append("```cs\nvar x = 1;\n```\n"));
        Assert.Contains("▌cs", output);
        Assert.Contains("var x = 1;", output);
    }

    [Fact]
    public void BadgeDoesNotLeakIntoTheCodeBody()
    {
        var renderer = new ConsoleRenderer(true);
        var output = Capture(() => renderer.Append("```cs\nvar x = 1;\n```\n"));
        Assert.DoesNotContain("cs\nvar x", output);
    }

    [Fact]
    public void LanguageDoesNotLeakToTheNextBlock()
    {
        // 上一块的语言不能出现在下一块上：两块语言不同最容易被发现
        var renderer = new ConsoleRenderer(true);
        var output = Capture(() => renderer.Append("```cs\nvar a = 1;\n```\n\n```json\n{}\n```\n"));
        Assert.Contains("  ▌cs", output);
        Assert.Contains("  ▌json", output);
        Assert.DoesNotContain("▌csjson", output);
    }

    [Fact]
    public void PlainFenceHasNoBadge()
    {
        var renderer = new ConsoleRenderer(true);
        var output = Capture(() => renderer.Append("```\nplain\n```\n"));
        Assert.DoesNotContain("▌", output);
    }

    [Fact]
    public void InlineCodeIsNotTreatedAsAFence()
    {
        var renderer = new ConsoleRenderer(true);
        var output = Capture(() => renderer.Append("使用 ```literal``` 文本\n"));
        Assert.DoesNotContain("▌", output);
    }
}
