using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>状态栏渲染回归：窄终端下按优先级丢段，且任何宽度都不溢出（CJK 按 2 列计）。</summary>
public class StatusBarTests
{
    private const string Mode = "code";
    private const string Model = "gpt-5";
    private const string Cwd = "CodeAgent";
    private const string Branch = "main";
    private const string TurnIn = "1.2K";
    private const string TurnOut = "340";
    private const string Ctx = "ctx 8.1K/200K (4%)";
    private const string Think = "think:high";

    private static string Build(int width) =>
        Program.BuildStatusBar(Mode, Model, Cwd, Branch, TurnIn, TurnOut, Ctx, Think, width);

    [Fact]
    public void StatusBar_Wide_KeepsEverySegment()
    {
        Assert.Equal(
            "⏵ code · gpt-5 · CodeAgent (main) · 1.2K in / 340 out · ctx 8.1K/200K (4%) · think:high",
            Build(0));
        Assert.Equal(
            "⏵ code · gpt-5 · CodeAgent (main) · 1.2K in / 340 out · ctx 8.1K/200K (4%) · think:high",
            Build(200));
    }

    [Fact]
    public void StatusBar_NarrowDropsThinkFirst()
    {
        var text = Build(60);
        Assert.DoesNotContain("think:", text);
        Assert.Contains("(main)", text);
        Assert.Contains("ctx", text);
    }

    [Fact]
    public void StatusBar_NarrowerDropsTurnTokensThenCtx()
    {
        var text = Build(34);
        Assert.DoesNotContain("think:", text);
        Assert.DoesNotContain(" out", text);
        Assert.DoesNotContain("ctx", text);
        Assert.Contains("(main)", text);

        var shorter = Build(22);
        Assert.DoesNotContain("(main)", shorter);
        Assert.Contains("⏵ code", shorter);
    }

    [Fact]
    public void StatusBar_EmptyOptionalSegmentsOmitted()
    {
        Assert.Equal(
            "⏵ code · gpt-5 · CodeAgent · 1.2K in / 340 out · ctx 8.1K/200K (4%)",
            Program.BuildStatusBar(Mode, Model, Cwd, null, TurnIn, TurnOut, Ctx, "", 0));
        Assert.Equal(
            "⏵ code · gpt-5 · CodeAgent (main) · 1.2K in / 340 out · ctx 8.1K/200K (4%)",
            Program.BuildStatusBar(Mode, Model, Cwd, Branch, TurnIn, TurnOut, Ctx, "", 0));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(140)]
    public void StatusBar_NeverExceedsTerminalWidth(int width)
    {
        Assert.True(TextUtil.DisplayWidth(Build(width)) <= width, $"宽度 {width} 溢出");
    }

    [Fact]
    public void StatusBar_CjkSegmentsCountedAsDoubleWidth()
    {
        // 「代码」两字按 2 列/字计，34 列只能放下 head+目录，多余段必须丢弃
        var text = Program.BuildStatusBar("代码", Model, Cwd, Branch, TurnIn, TurnOut, Ctx, Think, 34);
        Assert.True(TextUtil.DisplayWidth(text) <= 34);
        Assert.Contains("代码", text);
    }
}
