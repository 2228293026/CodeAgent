using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 确认/告警行末尾路径的窄屏降级。
/// 回归点：此前整段正文按显示宽度硬截**尾部**，于是
/// `✔ 已保存会话: C:\Users\me\.codeagent\sessions\2026-09-26.json` 被截成
/// `✔ 已保存会话: C:\Users\me\.co…` —— 路径**就是**这句话的重点，
/// 切掉尾部等于把「存到哪儿了」这个信息整个丢掉。
/// </summary>
public class NoticeTrailingPathTests
{
    private const string Body = "已保存会话: C:/Users/me/.codeagent/sessions/2026-09-26-01.json";

    [Fact]
    public void WideNoticeIsUnchanged()
    {
        Assert.Equal($"✔ {Body}", Program.FormatConfirmLine(Body, 200));
    }

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        Assert.Equal($"✔ {Body}", Program.FormatConfirmLine(Body, 0));
    }

    [Fact]
    public void NarrowNoticeKeepsTheFileName()
    {
        var line = Program.FormatConfirmLine(Body, 40);
        Assert.Contains("2026-09-26-01.json", line);
    }

    [Fact]
    public void NarrowNoticeKeepsTheProse()
    {
        var line = Program.FormatConfirmLine(Body, 40);
        Assert.Contains("已保存会话", line);
    }

    [Fact]
    public void ShortenedPathIsMarked()
    {
        var line = Program.FormatConfirmLine(Body, 40);
        Assert.Contains("…", line);
    }

    [Fact]
    public void WarningLinesGetTheSameTreatment()
    {
        var line = Program.FormatNoticeLine($"导出失败: C:/out/reports/very/deep/report.md", 30, "⚠");
        Assert.StartsWith("⚠ 导出失败", line);
        Assert.Contains("report.md", line);
    }

    [Fact]
    public void ProseWithoutAPathUsesPlainTruncation()
    {
        var line = Program.FormatConfirmLine("这是一段没有路径的很长的确认文字内容需要被截断", 20);
        Assert.Contains("…", line);
        Assert.DoesNotContain("/", line);
    }

    [Fact]
    public void PathIsLeftAloneWhenItFits()
    {
        Assert.Null(Program.ShortenTrailingPath(Body, 200));
    }

    [Fact]
    public void NoRoomForAPathReturnsNull()
    {
        // 只够放正文：返回 null 交给普通截断，而不是塞进一个假路径
        Assert.Null(Program.ShortenTrailingPath(Body, 12));
    }

    [Fact]
    public void ZeroBudgetReturnsNull()
    {
        Assert.Null(Program.ShortenTrailingPath(Body, 0));
    }

    [Fact]
    public void EmptyBodyReturnsNull()
    {
        Assert.Null(Program.ShortenTrailingPath("", 40));
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var width in new[] { 8, 12, 16, 20, 30, 40, 60, 80 })
        {
            var line = Program.FormatConfirmLine(Body, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width,
                $"预算 {width} 实际 {TextUtil.DisplayWidth(line)}：{line}");
        }
    }

    [Fact]
    public void CjkProseHeadIsMeasuredByDisplayWidth()
    {
        // 中文正文按显示宽度算，否则路径预算会被低估
        var line = Program.FormatConfirmLine("已成功导出会话快照到 C:/out/报告/最终版.md", 32);
        Assert.True(TextUtil.DisplayWidth(line) <= 32, line);
    }
}
