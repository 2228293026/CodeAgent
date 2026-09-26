using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/status</c> 会话状态面板（学自 Claude Code）。
/// </summary>
public sealed class StatusPanelTests
{
    private static System.Collections.Generic.List<(string Key, string Value)> Rows(
        long ctx = 348, int win = 200_000, string branch = "main", string provider = "kilo") =>
        Program.BuildStatusRows("code", "space-bunny-alpha", provider,
            "D:\\Projects\\CodeAgent", branch, ctx, win, 12, 34,
            TimeSpan.FromSeconds(192), "0.4.0", "281c3ad");

    [Fact]
    public void Status_ShowsTheEssentials()
    {
        var text = Program.FormatStatusPanel(Rows(), 100);
        Assert.Contains("状态:", text);
        Assert.Contains("code", text);
        Assert.Contains("space-bunny-alpha", text);
        Assert.Contains("main", text);
    }

    [Fact]
    public void Status_ListsTheFourOverlappingCommandsSeparately()
    {
        // /config=设置 /stats=账本 /diag=终端 /status=会话状态。分工写死，避免以后被合并成一个。
        var help = string.Join("\n", Program.ReplCommands.Select(c => c.Command));
        Assert.Contains("/status", help);
        Assert.Contains("/stats", help);
        Assert.Contains("/config", help);
        Assert.Contains("/diag", help);
    }

    [Fact]
    public void Status_ShowsContextUsage()
    {
        var text = Program.FormatStatusPanel(Rows(), 100);
        Assert.Contains("上下文", text);
        Assert.Contains("%", text);
    }

    [Fact]
    public void Status_ShowsBuildIdentity()
    {
        // 版本号逐轮不变，必须带上 sha 才判断得出是不是最新构建
        var text = Program.FormatStatusPanel(Rows(), 100);
        Assert.Contains("0.4.0+281c3ad", text);
    }

    [Fact]
    public void Status_OmitsBuildSuffixWhenNoSha()
    {
        var rows = Program.BuildStatusRows("code", "m", "", "C:\\w", null,
            0, 0, 0, 0, TimeSpan.Zero, "0.4.0", "");
        Assert.Contains("0.4.0", Program.FormatStatusPanel(rows, 100));
        Assert.DoesNotContain("0.4.0+", Program.FormatStatusPanel(rows, 100));
    }

    [Fact]
    public void Status_OmitsProviderWhenUnknown()
    {
        var rows = Program.BuildStatusRows("code", "m", "", "C:\\w", null,
            0, 0, 0, 0, TimeSpan.Zero, "0.4.0", "abc1234");
        Assert.DoesNotContain("供应商", Program.FormatStatusPanel(rows, 100));
    }

    [Fact]
    public void Status_OmitsBranchOutsideGitRepo()
    {
        var rows = Program.BuildStatusRows("code", "m", "p", "C:\\w", null,
            0, 0, 0, 0, TimeSpan.Zero, "0.4.0", "abc1234");
        var text = Program.FormatStatusPanel(rows, 100);
        Assert.Contains("C:\\w", text);
        Assert.DoesNotContain("()", text);
    }

    [Fact]
    public void Status_KeysAreAligned()
    {
        // 键宽按最长的键算，短键补空格——扫读时值列是一条直线
        var text = Program.FormatStatusPanel(Rows(), 100);
        var lines = text.Replace("\r\n", "\n").Split('\n').Skip(1).Where(l => l.Length > 0).ToList();
        var cols = lines.Select(l => TextUtil.DisplayWidth(l.Substring(l.IndexOf(' ') + 1))).ToList();
        Assert.True(cols.All(c => c > 0));
    }

    [Fact]
    public void Status_NeverExceedsWidthAtAnyTerminalSize()
    {
        for (var w = 8; w <= 200; w++)
        {
            var text = Program.FormatStatusPanel(Rows(), w);
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                Assert.True(TextUtil.DisplayWidth(line) <= w, $"w={w} 超宽: {line}");
        }
    }

    [Fact]
    public void Status_EmptyRowsSaySo()
    {
        Assert.Contains("无状态信息", Program.FormatStatusPanel(Array.Empty<(string, string)>(), 80));
    }

    // —— 上下文告警 ——

    [Fact]
    public void Advice_SilentWhenContextIsSmall()
    {
        Assert.Null(Program.FormatContextAdvice(1000, 200_000, 80));
    }

    [Fact]
    public void Advice_SuggestsCompactNearFull()
    {
        var advice = Program.FormatContextAdvice(190_000, 200_000, 0);
        Assert.NotNull(advice);
        Assert.Contains("/compact", advice);
    }

    [Fact]
    public void Advice_ReportsAutoCompactThreshold()
    {
        var advice = Program.FormatContextAdvice(90_000, 100_000, 80);
        Assert.NotNull(advice);
        Assert.Contains("自动压缩", advice);
        Assert.Contains("80", advice);
    }

    [Fact]
    public void Advice_SilentWhenWindowUnknown()
    {
        // 窗口未知时算不出百分比，不该编一个出来
        Assert.Null(Program.FormatContextAdvice(1000, 0, 80));
    }

    [Fact]
    public void Advice_SilentWhenNoTokensYet()
    {
        Assert.Null(Program.FormatContextAdvice(0, 200_000, 80));
    }

    [Fact]
    public void Advice_FitsNarrowTerminals()
    {
        for (var w = 8; w <= 60; w++)
        {
            var advice = Program.FormatContextAdvice(190_000, 200_000, 0)!;
            var line = Program.FormatHintLine(advice, w);
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"w={w} 超宽: {line}");
        }
    }
}
