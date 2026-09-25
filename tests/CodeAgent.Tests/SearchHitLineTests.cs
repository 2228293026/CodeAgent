using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 会话搜索命中行（/find）。
/// 回归点：摘要曾写死 110 **字符** 截断——80 列终端溢出 30 列，
/// 而中文摘要在 110 字符（220 显示列）时就被砍掉一半，宽屏白白浪费。
/// </summary>
public class SearchHitLineTests
{
    [Fact]
    public void FormatSearchHitLine_UnknownWidth_KeepsLegacyBehavior()
    {
        var snippet = new string('x', 300);
        var line = Program.FormatSearchHitLine("user", snippet);
        Assert.StartsWith("  [user] ", line);
        // 宽度未知时维持 110 字符的原有观感：行明显短于原文且带省略标记
        Assert.True(line.Length < 140, $"未按 110 字符截断: {line.Length}");
        Assert.EndsWith("…", line);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void FormatSearchHitLine_NeverExceedsWidth(int width)
    {
        var line = Program.FormatSearchHitLine("assistant", new string('y', 500), width);
        Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
    }

    [Fact]
    public void FormatSearchHitLine_ShortSnippetStaysWhole()
    {
        var line = Program.FormatSearchHitLine("user", "简短命中", 80);
        Assert.Equal("  [user] 简短命中", line);
    }

    [Fact]
    public void FormatSearchHitLine_WideTerminalShowsMoreThanNarrow()
    {
        var snippet = new string('z', 400);
        var narrow = Program.FormatSearchHitLine("user", snippet, 60);
        var wide = Program.FormatSearchHitLine("user", snippet, 180);
        Assert.True(wide.Length > narrow.Length);
    }

    [Fact]
    public void FormatSearchHitLine_CjkSnippetUsesDisplayWidthNotCharCount()
    {
        // 200 个汉字 = 400 显示列：80 列终端应只剩约 33 字，而不是显示 70 个字
        var snippet = string.Concat(Enumerable.Repeat("中", 200));
        var line = Program.FormatSearchHitLine("user", snippet, 80);
        Assert.True(TextUtil.DisplayWidth(line) <= 80, $"溢出: {TextUtil.DisplayWidth(line)}");
        Assert.True(snippet.Length - line.Length > 100, $"按字符数截断了，实际少 {snippet.Length - line.Length}");
    }

    [Fact]
    public void FormatSearchHitLine_KeepsRoleTagVisible()
    {
        // 放得下角色标记时必须完整可见
        foreach (var width in new[] { 20, 40, 80, 140 })
        {
            var line = Program.FormatSearchHitLine("assistant", "内容", width);
            Assert.StartsWith("  [assistant] ", line);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(14)]
    public void FormatSearchHitLine_NarrowerThanTagStillStaysInWidth(int width)
    {
        // 比角色标记还窄时只能裁剪标记本身，但绝不允许溢出
        var line = Program.FormatSearchHitLine("assistant", "内容", width);
        Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
    }
}
