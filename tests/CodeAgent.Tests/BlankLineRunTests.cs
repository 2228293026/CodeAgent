using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 连续空行收敛：模型常连发 3–5 个空行，纯占屏且把段落节奏切碎。
/// 空行必须暂存——流式渲染要等下一行才知道前面是不是"连发空行"。
/// </summary>
[Collection("ConsoleOutput")]
public class BlankLineRunTests
{
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

    [Fact]
    public void SingleBlankLine_IsPreserved()
    {
        var output = Render("上\n\n下\n");
        var lines = output.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("上", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("下", lines[2]);
    }

    [Fact]
    public void ConsecutiveBlankLines_CollapseToMaxBlankRun()
    {
        var output = Render("上\n\n\n\n\n下\n");
        var lines = output.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("上", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("下", lines[2]);
        Assert.Equal(1, ConsoleRenderer.MaxBlankRun);
    }

    [Fact]
    public void TrailingBlankLines_AreClampedAtFlush()
    {
        var output = Render("上\n\n\n\n");
        var trailing = output.Replace("\r\n", "\n").Split('\n');
        // 末尾只允许 MaxBlankRun 个空行，而不是把模型多打的空行原样刷屏
        var blanks = 0;
        for (var i = trailing.Length - 1; i >= 0 && trailing[i].Length == 0; i--)
            blanks++;
        Assert.True(blanks <= ConsoleRenderer.MaxBlankRun + 1, $"尾部空行 {blanks} 个未收敛");
    }

    [Fact]
    public void BlankLinesInsideCodeBlock_AreNotCollapsed()
    {
        // 代码块内的空行是代码内容，不能收敛
        var output = Render("```\na\n\n\n\nb\n```\n");
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var a = Array.IndexOf(lines, "a");
        Assert.True(a >= 0);
        Assert.Equal("b", lines[a + 4]);
    }

    [Fact]
    public void BlankLinesBeforeTable_AreFlushedOnce()
    {
        var output = Render("上\n\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var header = lines.FirstOrDefault(l => l.Contains('a') && l.Contains('│'));
        Assert.NotNull(header);
        Assert.StartsWith("  ", header);
    }

    [Fact]
    public void NormalParagraphsKeepSingleBlankSeparator()
    {
        var output = Render("甲\n\n乙\n\n丙\n");
        var lines = output.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("甲", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("乙", lines[2]);
        Assert.Equal("", lines[3]);
        Assert.Equal("丙", lines[4]);
    }
}
