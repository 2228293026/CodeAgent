using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 提示符单行判定（决定切换块原地覆盖 vs 追加）。
/// 回归点：旧实现用字符串 .Length（字符数）比较，中文模式名按 1 字符计却占 2 列，
/// 会把会折行的提示符误判为放得下；且宽度读取失败时默认 true，等于按未知几何原地覆盖。
/// </summary>
public class PromptFitTests
{
    private const string AsciiPrompt = "[code|gpt-5] CodeAgent> ";
    private const string CjkPrompt = "[代码模式|通义千问] 项目目录> ";

    [Fact]
    public void PromptFitsOneRow_AsciiPromptOnWideTerminal_Fits()
    {
        Assert.True(Program.PromptFitsOneRow(AsciiPrompt, 80));
    }

    [Fact]
    public void PromptFitsOneRow_CjkPromptUsesDisplayWidthNotCharCount()
    {
        // 18 字符却占 30 列：按 .Length 的旧规则在 30 列终端上判定「放得下」（18 < 22），
        // 实际提示符 30 列已折行，\x1b[3A 回到的块顶算错
        Assert.Equal(18, CjkPrompt.Length);
        Assert.Equal(30, TextUtil.DisplayWidth(CjkPrompt));
        Assert.False(Program.PromptFitsOneRow(CjkPrompt, 30));
        // 终端足够宽时（30 + 余量 8）才放得下
        Assert.True(Program.PromptFitsOneRow(CjkPrompt, 38));
        Assert.True(Program.PromptFitsOneRow(CjkPrompt, 80));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PromptFitsOneRow_UnknownWidth_IsNotAssumedToFit(int width)
    {
        // 原地覆盖依赖确定行几何；宽度未知时应走安全的追加模式
        Assert.False(Program.PromptFitsOneRow(AsciiPrompt, width));
    }

    [Fact]
    public void PromptFitsOneRow_NarrowTerminal_False()
    {
        Assert.False(Program.PromptFitsOneRow(AsciiPrompt, 20));
    }

    [Fact]
    public void PromptFitMargin_HasNamedConstant() => Assert.Equal(8, Program.PromptFitMargin);

    [Fact]
    public void PromptFitsOneRow_MarginReservesHeadroom()
    {
        // 恰好等于余量的边界：放得下
        var exact = TextUtil.DisplayWidth(AsciiPrompt) + Program.PromptFitMargin;
        Assert.True(Program.PromptFitsOneRow(AsciiPrompt, exact));
        // 少一列即放不下
        Assert.False(Program.PromptFitsOneRow(AsciiPrompt, exact - 1));
    }
}
