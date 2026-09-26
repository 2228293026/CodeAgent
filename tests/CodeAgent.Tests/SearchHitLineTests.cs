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
    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    public void FormatResultLine_KeywordBearingLinesFit(int width)
    {
        // /find 会把用户输入的关键字与快照名直接拼进行里，长度不受控
        var bodies = new[]
        {
            $"历史会话中没有匹配「{new string('词', 200)}」的内容。",
            $"快照 {new string('名', 200)} · 3 分钟前（/load {new string('名', 200)} 恢复）:",
            $"{new string('f', 200)} · 2 小时前（/resume 可恢复）:",
            "用法: /find <关键字> —— 在历史会话日志里搜索内容（与 /resume 同源，最新在前）",
            "…（仅显示前 5 个命中文件，更精确的关键字可减少噪音）",
        };
        foreach (var body in bodies)
        {
            var line = Program.FormatResultLine(body, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
        }
    }

    [Fact]
    public void FormatHintLine_RealHintsFit()
    {
        // 这些提示行此前都是裸字符串，最长的一条在 40 列终端上会硬折行
        var hints = new[]
        {
            "Shift+Tab 或 /access next 循环切换; /access <strict|whitelist|full> 直接指定",
            "（服务未返回任何模型：检查 baseUrl 是否指向支持 /models 的端点、API Key 是否有列表权限；仍可直接 /model <名称> 使用）",
            "提示: /model <编号> 可直接切换",
            "* = 当前配置的模型",
            "输入 /help 查看命令；直接输入任务描述即可开始。",
            "仍将按输入保存；/models [关键字] 可查列表",
        };
        foreach (var hint in hints)
        {
            foreach (var width in new[] { 30, 40, 60, 80, 120 })
            {
                var line = Program.FormatHintLine(hint, width);
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
                Assert.StartsWith("  ", line);
            }
        }
    }

    [Fact]
    public void FormatHintLine_UnknownWidthKeepsTwoSpaceIndent()
    {
        Assert.Equal("  * = 当前配置的模型", Program.FormatHintLine("* = 当前配置的模型"));
    }

    [Fact]
    public void FormatResultLine_UnknownWidthKeepsFullText()
    {
        var body = $"历史会话中没有匹配「{new string('词', 200)}」的内容。";
        Assert.Equal(body, Program.FormatResultLine(body));
    }

    [Fact]
    public void FormatResultLine_ShortTextIsUnchanged()
    {
        Assert.Equal("没有可搜索的会话记录。", Program.FormatResultLine("没有可搜索的会话记录。", 80));
    }
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
