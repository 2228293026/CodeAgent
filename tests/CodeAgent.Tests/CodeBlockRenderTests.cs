using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 代码块渲染规整：Tab 展开、去掉行尾空白、CRLF 归一。
/// 回归点：代码原样输出，制表符按终端默认 8 列渲染，缩进层级参差；行尾空白还会污染复制粘贴与回滚缓冲。
/// 行首缩进必须原样保留——那是代码语义。
/// </summary>
public class CodeBlockRenderTests
{
    [Fact]
    public void ExpandCodeTabs_NoTabUnchanged()
    {
        Assert.Equal("    var x = 1;", ConsoleRenderer.ExpandCodeTabs("    var x = 1;"));
    }

    [Fact]
    public void ExpandCodeTabs_ExpandsToFourColumnStops()
    {
        Assert.Equal("    x", ConsoleRenderer.ExpandCodeTabs("\tx"));
        // 两列后遇 Tab：补 2 格到下一个 4 的倍数
        Assert.Equal("    x", ConsoleRenderer.ExpandCodeTabs("  \tx"));
        Assert.Equal("    x   y", ConsoleRenderer.ExpandCodeTabs("\t" + "x  \ty"));
    }

    [Fact]
    public void ExpandCodeTabs_TabStopsAreColumnBased()
    {
        // 3 个字符后遇到 Tab：推进到下一个 4 的倍数（+1）
        Assert.Equal("abc d", ConsoleRenderer.ExpandCodeTabs("abc\td"));
        // 4 个字符后遇到 Tab：正好推进一步（+4）
        Assert.Equal("abcd    e", ConsoleRenderer.ExpandCodeTabs("abcd\te"));
    }

    [Fact]
    public void ExpandCodeTabs_CountsCjkAsTwoColumns()
    {
        // 两个中文字符 = 4 列，Tab 正好推进一步（+4）
        Assert.Equal("中文    x", ConsoleRenderer.ExpandCodeTabs("中文\tx"));
        // 一个中文字符 = 2 列，Tab 补 2 格
        Assert.Equal("中  x", ConsoleRenderer.ExpandCodeTabs("中\tx"));
    }

    [Fact]
    public void ExpandCodeTabs_SurrogatePairIsTwoColumns()
    {
        var emoji = "\U0001F600";
        Assert.Equal(emoji + "  x", ConsoleRenderer.ExpandCodeTabs(emoji + "\tx"));
    }

    [Fact]
    public void NormalizeCodeBlock_TrimsTrailingWhitespace()
    {
        // 末尾换行保留（末元素为空串），只清行尾空白
        Assert.Equal("a\nb\n", ConsoleRenderer.NormalizeCodeBlock("a   \nb\t\n"));
        Assert.Equal("a\nb", ConsoleRenderer.NormalizeCodeBlock("a   \nb"));
    }

    [Fact]
    public void NormalizeCodeBlock_PreservesLeadingIndentation()
    {
        var code = "if (x)\n{\n    return 1;\n}";
        Assert.Equal(code, ConsoleRenderer.NormalizeCodeBlock(code));
    }

    [Fact]
    public void NormalizeCodeBlock_NormalizesCrlf()
    {
        Assert.Equal("a\nb\n", ConsoleRenderer.NormalizeCodeBlock("a\r\nb\r\n"));
    }

    [Fact]
    public void NormalizeCodeBlock_EmptyInputUnchanged()
    {
        Assert.Equal("", ConsoleRenderer.NormalizeCodeBlock(""));
    }

    [Fact]
    public void CodeTabWidth_HasNamedConstant() => Assert.Equal(4, ConsoleRenderer.CodeTabWidth);
}
