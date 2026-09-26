using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 代码块的围栏语言徽标。
/// 回归点：围栏语言标注（```cs 的 cs）此前被**直接丢弃**——读者完全无从判断
/// 下面这段是什么语言的代码，只能靠猜。而徽标本身也可能放不下：
/// 窄屏下让一行徽标自己折行，会把整个代码块顶歪，所以放不下时必须整行省略。
/// </summary>
[Collection("ConsoleOutput")]
public class CodeLangBadgeTests
{
    /// <summary>捕获渲染输出。显式固定在「颜色开启」：徽标只在颜色路径下出现，
    /// 关闭颜色时代码块改用 Markdown 围栏（见 CodeBlockWithoutColorTests）。
    /// `dotnet test` 的输出本就重定向，不写死这个前提就会测到另一条分支。</summary>
    private static string Capture(Action action)
    {
        using var colour = ColourTestScope.On();
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
        // 按**行**断言，不匹配 "cs\nvar x" 这种跨行子串：
        // 它只在 Unix 的 \n 下成立，Windows 的 \r\n 永远匹配不到。
        var lines = ConsoleRendererTests.OutputLines(output);
        Assert.Contains("  ▌cs", lines);
        Assert.Contains("var x = 1;", lines);
        // 徽标行之后的每一行都不得再出现语言标注（徽标行本身含 cs 是正常的）
        var badge = Array.FindIndex(lines, l => l.Contains('▌'));
        Assert.True(badge >= 0, "缺少语言徽标");
        Assert.DoesNotContain(lines.Skip(badge + 1), l => l.Contains("cs", StringComparison.Ordinal));
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
