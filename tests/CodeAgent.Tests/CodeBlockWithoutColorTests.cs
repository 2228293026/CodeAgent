using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 围栏代码块在**没有颜色**时的边界。
/// 回归点：代码块此前唯一的边界是「▌cs」徽标，颜色一关闭（NO_COLOR / TERM=dumb /
/// 输出重定向）就只剩一行普通文字 —— 代码与散文**完全无法区分**，
/// 读者无从判断这段输出到底是代码还是段落。
/// 现在关闭颜色时还原 Markdown 围栏（```lang … ```）：既标明语言，也给出明确起止。
/// </summary>
[Collection("ConsoleOutput")]
public class CodeBlockWithoutColorTests
{
    private static string Render(string markdown, bool colour)
    {
        var scope = colour ? ColourTestScope.On() : ColourTestScope.Off();
        var writer = new StringWriter();
        var stdout = Console.Out;
        try
        {
            using (scope)
            {
                Console.SetOut(writer);
                var renderer = new ConsoleRenderer(true);
                renderer.SetWidth(120);
                renderer.Append(markdown);
                renderer.Flush();
            }
        }
        finally { Console.SetOut(stdout); }
        return writer.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void FencesAreRestoredWhenColourIsOff()
    {
        var output = Render("```csharp\nvar x = 1;\n```\n", false);
        // 用「非空行」而不是固定下标：输出末尾常带一个空行，固定下标会取到它
        var lines = ConsoleRendererTests.OutputLines(output).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("```csharp", lines[0]);
        Assert.Equal("var x = 1;", lines[1]);
        Assert.Equal("```", lines[2]);
    }

    [Fact]
    public void ColourPathKeepsTheBadgeAndNoFence()
    {
        var output = Render("```csharp\nvar x = 1;\n```\n", true);
        Assert.DoesNotContain("```", output);
        Assert.Contains("▌", output);
    }

    [Fact]
    public void CodeIsDistinguishableFromProseWhenColourIsOff()
    {
        var output = Render("看这段代码：\n\n```csharp\nvar x = 1;\n```\n", false);
        // 散文行没有任何围栏标记，代码行有——两者必须能区分
        var prose = ConsoleRendererTests.OutputLines(output).First(l => l.Contains("看这段代码", StringComparison.Ordinal));
        Assert.DoesNotContain("```", prose);
        Assert.Contains("```", output);
    }

    [Fact]
    public void FenceLanguageIsPreserved()
    {
        foreach (var lang in new[] { "csharp", "bash", "json", "" })
        {
            var output = Render($"```{lang}\nkey\n```\n", false);
            var open = ConsoleRendererTests.OutputLines(output).First();
            Assert.StartsWith("```", open);
        }
    }

    [Fact]
    public void UnknownWidthStillFences()
    {
        // 宽度未知也要有边界：这是语义，不是排版
        using var colour = ColourTestScope.Off();
        var writer = new StringWriter();
        var stdout = Console.Out;
        try
        {
            Console.SetOut(writer);
            var renderer = new ConsoleRenderer(true);
            renderer.Append("```csharp\nvar x = 1;\n```\n");
            renderer.Flush();
        }
        finally { Console.SetOut(stdout); }
        var output = writer.ToString().Replace("\r\n", "\n");
        Assert.Contains("```csharp", output);
        Assert.Contains("var x = 1;", output);
    }

    [Fact]
    public void NoEscapeSequencesWhenColourIsOff()
    {
        var output = Render("```csharp\nvar x = 1;\n```\n", false);
        Assert.DoesNotContain('', output);
    }
}
