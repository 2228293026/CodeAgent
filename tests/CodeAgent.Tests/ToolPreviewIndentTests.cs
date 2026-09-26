using System;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具输出预览的整块缩进。
/// 回归点：此前写成 `Console.WriteLine("      " + preview)`，只给**整个字符串**加了一次前缀，
/// 于是只有首行有缩进——命令输出通常是多行的，续行顶到行首，
/// 预览块会「逃出」它所属的 ✔/⚠ 工具状态行，看起来像顶层输出。
/// </summary>
public class ToolPreviewIndentTests
{
    [Fact]
    public void IndentBlock_IndentsEveryLine()
    {
        var text = "第一行\n第二行\n第三行";
        var indented = TextUtil.IndentBlock(text, "  ");
        Assert.Equal("  第一行\n  第二行\n  第三行", indented);
    }

    [Fact]
    public void IndentBlock_HandlesCrlf()
    {
        Assert.Equal("  a\n  b", TextUtil.IndentBlock("a\r\nb", "  "));
    }

    [Fact]
    public void IndentBlock_LeavesBlankLinesUnpadded()
    {
        // 空行加尾随空格会在复制粘贴时带出一堆无意义空白
        var indented = TextUtil.IndentBlock("a\n\nb", "  ");
        Assert.Equal("  a\n\n  b", indented);
    }

    [Fact]
    public void IndentBlock_EmptyInputsAreIdentity()
    {
        Assert.Equal("", TextUtil.IndentBlock("", "  "));
        Assert.Equal("a\nb", TextUtil.IndentBlock("a\nb", ""));
    }

    [Fact]
    public void ToolPreviewIndentIsSixColumns()
    {
        Assert.Equal(6, TextUtil.DisplayWidth(AgentClass.ToolPreviewIndent));
    }

    [Fact]
    public void PreviewStaysContainedUnderStatusLine()
    {
        // 整块缩进后，预览的每一行都与首行起始列一致
        var preview = "dotnet build 成功\n    0 个警告\n    0 个错误";
        var indented = TextUtil.IndentBlock(preview, AgentClass.ToolPreviewIndent);
        foreach (var line in indented.Split('\n'))
            Assert.StartsWith(AgentClass.ToolPreviewIndent, line);
    }

    [Fact]
    public void SingleLinePreviewBehavesLikeBefore()
    {
        // 单行时与旧的 "      " + preview 完全一致，观感不变
        var preview = "一行输出";
        Assert.Equal(AgentClass.ToolPreviewIndent + preview,
            TextUtil.IndentBlock(preview, AgentClass.ToolPreviewIndent));
    }
}
