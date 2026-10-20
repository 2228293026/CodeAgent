using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 四个及以上反引号的代码围栏。
///
/// Markdown 规范允许用更长的围栏去**包住**更短的——这正是「教别人怎么写
/// 代码块」的写法，模型经常这么答：
/// <code>
/// ````markdown
/// ```cs
/// var x = 1;
/// ```
/// ````
/// </code>
///
/// 渲染器此前只认到第 3 个反引号：外层第 4 个被当成语言标注（得到 "`markdown"），
/// 内层的 ``` 又把外层提前关掉，剩下的内容被当正文重新排版。
/// </summary>
[Collection("ConsoleOutput")]
public class CodeFenceLengthTests
{
    private static string Render(string markdown, bool colour = false)
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
                renderer.SetWidth(100);
                renderer.Append(markdown);
                renderer.Flush();
            }
        }
        finally { Console.SetOut(stdout); }
        return writer.ToString().Replace("\r\n", "\n");
    }

    private static string[] NonEmptyLines(string s) =>
        s.Split('\n').Where(l => l.Trim().Length > 0).ToArray();

    [Fact]
    public void ThreeTicksStillWork()
    {
        // 基本盘：普通三反引号代码块。关色时还原围栏（这是有意的设计，
        // 没有颜色时代码与散文必须仍能区分）。
        var lines = NonEmptyLines(Render("```cs\nvar x = 1;\n```\n"));
        Assert.Equal("```cs", lines[0]);
        Assert.Equal("var x = 1;", lines[1]);
        Assert.Equal("```", lines[2]);
    }

    [Fact]
    public void FourTicksSwallowTheFourthTickFromTheLanguage()
    {
        // 核心断言：语言标注只能是 markdown，不能是 "`markdown"。
        // 这一个字符泄露，就说明第 4 个反引号被当成内容了。
        var lines = NonEmptyLines(Render("````markdown\n```cs\nvar x = 1;\n```\n````\n"));
        Assert.Equal("```markdown", lines[0]);
        Assert.DoesNotContain("`markdown", lines[0].TrimStart('`'));
    }

    [Fact]
    public void InnerFenceDoesNotCloseTheOuterOne()
    {
        // 内层 ``` 必须作为**内容**原样留在代码块里，不能把外层关掉。
        // 关掉的表现是：内层之后的 var x = 1; 被当成正文重新排版——
        // 它前面的 ``` 会消失（正文里围栏不是语法）。
        var lines = NonEmptyLines(Render("````markdown\n```cs\nvar x = 1;\n```\n````\n"));
        Assert.Contains("```cs", lines);
        Assert.Contains("var x = 1;", lines);
        // 内层收尾那一行也必须是 ```（内容），且它出现在 var 之后、外层 ```` 之前
        var innerClose = Array.IndexOf(lines, "```");
        var body = Array.IndexOf(lines, "var x = 1;");
        Assert.True(innerClose > body, $"内层收尾应在正文之后，实际 {innerClose} vs {body}");
    }

    [Fact]
    public void CodeAfterTheBlockIsNormalProse()
    {
        // 外层关掉之后的那句话必须是正文——不能被吞进代码块
        var lines = NonEmptyLines(Render("````markdown\n```cs\nvar x = 1;\n```\n````\n结束语。\n"));
        Assert.Equal("结束语。", lines[^1]);
    }

    [Fact]
    public void EmptyBlockStillCloses()
    {
        // 回归：修四个反引号时把 _tickRun 留在 3 忘了归零，
        // 空代码块 ```` ```\n``` ```` 于是关不掉、整段回复都被当代码。
        var lines = NonEmptyLines(Render("```\n```\n"));
        Assert.Equal(2, lines.Length);
        Assert.Equal("```", lines[0]);
        Assert.Equal("```", lines[1]);
    }
}
