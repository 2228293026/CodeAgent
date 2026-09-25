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
        Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
    }

    [Fact]
    public void FormatConfirmLine_SaveLoadExportShapesFit()
    {
        // /save、/load、/export 的确认行：会话名与导出路径都可能很长
        var bodies = new[]
        {
            $"已保存会话: {new string('会', 60)}",
            $"已恢复会话: {new string('a', 200)}",
            $"{new string('b', 200)} → exports/very/deep/path/session-2026-01-01.md",
            "上下文已达 92%（autoCompactPercent=90），已自动压缩历史。",
        };
        foreach (var body in bodies)
        {
            foreach (var width in new[] { 30, 60, 120 })
            {
                var line = Program.FormatConfirmLine(body, width);
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
                Assert.StartsWith("✔ ", line);
            }
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    public void FormatNoticeLine_StartupErrorShapesFit(int width)
    {
        // 启动期错误常带完整路径与异常消息，此前是裸字符串、没有宽度预算
        var bodies = new[]
        {
            $"参数错误: {new string('x', 300)}",
            $"目录不存在: {new string('d', 200)}",
            $"配置加载失败: {new string('e', 250)}",
            $"未知 provider「{new string('p', 200)}」（可用: a, b, c）",
            "任务为空：请提供任务描述（codeagent \"任务\"），或直接运行 codeagent 进入交互模式。",
        };
        foreach (var body in bodies)
        {
            var line = Program.FormatNoticeLine(body, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
            Assert.StartsWith("⚠ ", line);
        }
    }

    [Fact]
    public void FormatNoticeLine_ConfigWarningShapeFits()
    {
        var line = Program.FormatNoticeLine($"配置: {new string('w', 200)} 未被识别（拼写错误？）", 50);
        Assert.StartsWith("⚠ 配置: ", line);
        Assert.True(TextUtil.DisplayWidth(line) <= 50, $"溢出: {line}");
    }

    [Theory]
    [InlineData("已取消。", 40)]
    [InlineData("已取消（保持当前模式）。", 24)]
    [InlineData("已取消压缩（历史未变动）。", 16)]
    [InlineData("已取消配置向导。", 12)]
    [InlineData("已取消保存。", 8)]
    public void FormatCancelLine_NeverExceedsWidth(string body, int width)
    {
        var line = Program.FormatCancelLine(body, width);
        Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
        Assert.StartsWith("⏹ ", line);
    }

    [Theory]
    [InlineData("已取消。")]
    [InlineData("已取消（保持当前模式）。")]
    [InlineData("已取消压缩（历史未变动）。")]
    [InlineData("已取消配置向导。")]
    [InlineData("已取消保存。")]
    public void FormatCancelLine_KeepsFullBodyWhenWidthIsUnknown(string body)
    {
        var line = Program.FormatCancelLine(body);
        Assert.Equal($"⏹ {body}", line);
    }

    [Fact]
    public void FormatCancelLine_MarkerIsDistinctFromOutcomeMarkers()
    {
        // ⏹ 取消 / ✔ 成功 / ⚠ 错误 —— 三类结局必须一眼可分
        Assert.StartsWith("⏹ ", Program.FormatCancelLine("已取消。", 60));
        Assert.StartsWith("✔ ", Program.FormatConfirmLine("已保存。", 60));
        Assert.StartsWith("⚠ ", Program.FormatNoticeLine("出错了。", 60));
    }

    [Fact]
    public void FormatCancelLine_ResumeMarkerIsPreserved()
    {
        // /resume 用 ↩ 标记恢复，与 ✔ 区分
        var line = Program.FormatConfirmLine("已恢复会话: 20260101-120000.json", 60, "↩");
        Assert.StartsWith("↩ ", line);
        Assert.Contains("20260101-120000.json", line);
    }
}
