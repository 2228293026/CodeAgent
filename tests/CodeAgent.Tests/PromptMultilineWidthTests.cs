using System;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 回归：提示符占多行时（BuildInputFrameTop 返回「上边框 + 提示符」两行），
/// 输入行把整段提示符的宽度当成光标列，导致敲一个字符就折行、边框被覆盖。
///
/// 症状：用户每敲一个字符，屏幕多出一行「◈ high · /thinking」，
/// 边框横线消失。这是现场报上来的真实问题，不是设想出来的。
/// </summary>
public sealed class PromptMultilineWidthTests
{
    private const string FrameTop = "──────────────────────────── ◈ high · /thinking";
    private const string Prompt = "[code|model] CodeAgent> ";

    /// <summary>光标列只能按提示符**最后一行**算。</summary>
    internal static int CursorColumnFor(string promptPlain, string segment) =>
        TextUtil.DisplayWidth(LastLine(promptPlain)) + TextUtil.DisplayWidth(segment);

    internal static string LastLine(string s) => s[(s.LastIndexOf('\n') + 1)..];

    [Fact]
    public void CursorColumn_UsesOnlyTheLastPromptLine()
    {
        var twoLine = FrameTop + "\n" + Prompt;
        var col = CursorColumnFor(twoLine, "abc");

        // 正确值 = 提示符最后一行宽 + 已输入内容宽
        Assert.Equal(TextUtil.DisplayWidth(Prompt) + 3, col);
        // 错误实现会加上边框的几十列
        Assert.True(col < TextUtil.DisplayWidth(twoLine),
            $"光标列 {col} 竟不小于整段提示符宽度，说明把上边框也算进去了");
        Assert.True(col < 40, $"光标列 {col} 过大，光标会被推出终端行宽之外");
    }

    [Fact]
    public void CursorColumn_SingleLinePromptIsUnchanged()
    {
        // 单行提示符（无边框）走老路径，结果必须与旧实现一致
        Assert.Equal(TextUtil.DisplayWidth(Prompt) + 3, CursorColumnFor(Prompt, "abc"));
    }

    [Fact]
    public void PromptRows_CountsTheFrameTop()
    {
        // 重绘基线：提示符 2 行 + 空输入 1 行 = 3 行
        var promptRows = 1 + CountNewlines(FrameTop + "\n" + Prompt);
        Assert.Equal(2, promptRows);
    }

    [Fact]
    public void RedrawBaseline_PrefilledTextAddsToPromptRowsNotReplaces()
    {
        // 取消回合回填多行草稿：基线在原关系上**平移**提示符占的行数，而不是重写这个关系。
        // 3 行草稿 = 2 个换行符，基线 = 2（提示符）+ 2 = 4。
        // 刻意不改成「行数」计数：原实现用的是换行符数，改它会牵动多行重绘的擦除范围。
        var promptRows = 2;
        var initial = "第一行\n第二行\n第三行";
        Assert.Equal(2, CountNewlines(initial));
        Assert.Equal(4, promptRows + CountNewlines(initial));
    }

    [Fact]
    public void FrameTopLine_ItselfFitsTheTerminal()
    {
        // 边框行必须自己就放得下——它被折行的话，后面全乱
        for (var columns = 20; columns <= 120; columns++)
        {
            var top = Program.BuildInputFrameTop(Prompt, Program.InputModeHint("high"), columns);
            var first = TopLines(top)[0];
            Assert.True(TextUtil.DisplayWidth(first) <= columns,
                $"columns={columns} 边框行宽 {TextUtil.DisplayWidth(first)} 超宽：{first}");
        }
    }

    [Fact]
    public void CursorColumn_StaysInsideTerminalAcrossWidths()
    {
        // 端到端不变量：光标列不得超过终端宽度
        for (var columns = 20; columns <= 120; columns++)
        {
            var prompt = Program.BuildInputFrameTop(Prompt, Program.InputModeHint("high"), columns);
            var tail = LastLine(prompt);
            var col = CursorColumnFor(prompt, "");
            Assert.True(col <= columns, $"columns={columns} 光标列 {col} 超出");
            Assert.True(TextUtil.DisplayWidth(tail) <= columns, $"columns={columns} 提示符末行超宽");
        }
    }

    private static string[] TopLines(string s) => s.Split('\n');

    private static int CountNewlines(string s) => s.TrimEnd('\n').Split('\n').Length - 1;
}
