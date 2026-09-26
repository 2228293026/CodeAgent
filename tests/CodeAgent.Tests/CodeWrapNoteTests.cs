using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 代码块超长行的提示。
/// 回归点：代码**不能截断**（用户复制到的是残缺代码，比多占几列糟糕得多），
/// 但终端折行后「一行代码的后半段」和「下一条语句」长得一模一样，读者无从分辨。
/// </summary>
public class CodeWrapNoteTests
{
    [Fact]
    public void CountOverlongCodeLines_CountsOnlyOverlongLines()
    {
        var code = "var a = 1;\nvar b = 2;\n";
        Assert.Equal(0, ConsoleRenderer.CountOverlongCodeLines(code, 40));
    }

    [Fact]
    public void CountOverlongCodeLines_CountsLongLines()
    {
        var long1 = new string('x', 100);
        var code = $"var a = 1;\nvar b = {long1};\nvar c = 3;\n";
        Assert.Equal(1, ConsoleRenderer.CountOverlongCodeLines(code, 40));
    }

    [Fact]
    public void CountOverlongCodeLines_MeasuresDisplayWidthNotChars()
    {
        // 30 个汉字 = 60 列：按字符数只有 30，会被误判为放得下
        var code = "var 中文变量名 = \"" + new string('中', 30) + "\";\n";
        Assert.Equal(1, ConsoleRenderer.CountOverlongCodeLines(code, 40));
        Assert.Equal(0, ConsoleRenderer.CountOverlongCodeLines(code, 80));
    }

    [Fact]
    public void CountOverlongCodeLines_UnknownWidthCountsNone()
    {
        var code = new string('x', 500);
        Assert.Equal(0, ConsoleRenderer.CountOverlongCodeLines(code, 0));
    }

    [Fact]
    public void CountOverlongCodeLines_EmptyIsZero()
    {
        Assert.Equal(0, ConsoleRenderer.CountOverlongCodeLines("", 40));
    }

    [Fact]
    public void CodeWrapNote_MentionsCountAndWidth()
    {
        var note = ConsoleRenderer.CodeWrapNote(3, 80);
        Assert.Contains("3 行超过 80 列", note);
        // 明确告诉用户代码没有被截断——否则会担心复制到的是残缺内容
        Assert.Contains("代码本身未截断", note);
    }

    [Fact]
    public void CodeWrapNote_EmptyWhenNothingWrapped()
    {
        Assert.Equal("", ConsoleRenderer.CodeWrapNote(0, 80));
    }

    [Fact]
    public void CodeBlockIsNeverTruncated()
    {
        // 核心保证：超宽代码块原样输出，一个字符都不能少
        var code = "var b = " + new string('x', 300) + ";";
        var rendered = ConsoleRenderer.NormalizeCodeBlock(code);
        Assert.Equal(code, rendered);
        Assert.True(TextUtil.DisplayWidth(rendered) > 40);
    }

    [Fact]
    public void CodeWrapNoteOnlyCountsNonBlankLines()
    {
        var code = new string('x', 100) + "\n\n" + new string('y', 100);
        Assert.Equal(2, ConsoleRenderer.CountOverlongCodeLines(code, 40));
    }
}
