using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 告警行统一格式与宽度处理。
/// 回归点：告警原本是裸的 `⚠ ...` 字符串，没有宽度预算——正文一长就折行，
/// ⚠ 标记孤零零留在上一行，扫读时反而找不到告警。
/// </summary>
public class NoticeLineTests
{
    [Fact]
    public void FormatNoticeLine_Wide_KeepsFullBody()
    {
        Assert.Equal("⚠ 磁盘空间不足", Program.FormatNoticeLine("磁盘空间不足", 80));
    }

    [Fact]
    public void FormatNoticeLine_UnknownWidth_DoesNotTruncate()
    {
        var body = new string('x', 500);
        Assert.Equal($"⚠ {body}", Program.FormatNoticeLine(body, 0));
    }

    [Fact]
    public void FormatNoticeLine_TruncatesBodyButKeepsMarkerVisible()
    {
        var body = new string('字', 200); // 每个占 2 列
        var line = Program.FormatNoticeLine(body, 40);
        Assert.StartsWith("⚠ ", line);
        Assert.True(TextUtil.DisplayWidth(line) <= 40);
        Assert.EndsWith("…", line);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    public void FormatNoticeLine_NeverExceedsWidth(int width)
    {
        var line = Program.FormatNoticeLine(new string('中', 300), width);
        Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出");
    }

    [Fact]
    public void FormatNoticeLine_TooNarrowForBody_KeepsMarkerOnly()
    {
        // 宽度不足以放下任何正文：只留标记，绝不返回超宽行
        var line = Program.FormatNoticeLine("内容", 2);
        Assert.Equal("⚠", line);
    }

    [Fact]
    public void FormatNoticeLine_CustomMarker()
    {
        Assert.Equal("❗ 失败", Program.FormatNoticeLine("失败", 40, "❗"));
    }

    [Fact]
    public void FormatNoticeLine_EmptyBodyStillShowsMarker()
    {
        Assert.Equal("⚠ ", Program.FormatNoticeLine("", 40));
    }

    [Fact]
    public void FormatConfirmLine_UsesCheckMarkerByDefault()
    {
        Assert.Equal("✔ 已切换模型: gpt-5", Program.FormatConfirmLine("已切换模型: gpt-5", 80));
    }

    [Fact]
    public void FormatConfirmLine_KeepsMarkerVisibleWhenSavePathIsLong()
    {
        // 切换确认常附带完整保存路径；窄终端下标记必须留在行首
        var path = @"C:\Users\very\long\username\.codeagent\config\codeagent-config-with-long-name.json";
        var line = Program.FormatConfirmLine($"已切换模型: gpt-5，已保存到 {path}", 40);
        Assert.StartsWith("✔ ", line);
        Assert.True(TextUtil.DisplayWidth(line) <= 40);
        Assert.Contains("已切换模型", line);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(100)]
    public void FormatConfirmLine_NeverExceedsWidth(int width)
    {
        var line = Program.FormatConfirmLine($"已切换 Provider: openai，模型 gpt-5，已保存到 {new string('p', 300)}", width);
        Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出");
    }
}
