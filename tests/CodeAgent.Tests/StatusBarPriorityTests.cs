using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 状态栏分段降级的优先级。
/// 回归点：丢弃顺序此前是 think → turn → **ctx** → branch，然后才缩短目录。
/// 于是长路径场景下「上下文用了多少」——最该盯的动态信息——最先消失，
/// 屏幕上只剩一堆静态信息；而真正占地方的目录却排在它后面才被缩短。
/// </summary>
public class StatusBarPriorityTests
{
    private const string Mode = "plan";
    private const string Model = "gpt-5-codex";
    private const string Cwd = @"C:\Users\someone\projects\my-very-long-project-name\src\deep\nested";
    private const string Branch = "feature/some-long-branch-name";
    private const string TurnIn = "1.2K";
    private const string TurnOut = "840";
    private const string Ctx = "ctx 48K/200K (24%)";
    private const string Think = "think:high";

    private static string Build(int width, string ctx = Ctx, string think = Think, string? branch = Branch) =>
        Program.BuildStatusBar(Mode, Model, Cwd, branch, TurnIn, TurnOut, ctx, think, width);

    [Fact]
    public void WideTerminalShowsEverything()
    {
        var bar = Build(200);
        Assert.Contains(Mode, bar);
        Assert.Contains(Model, bar);
        Assert.Contains(Ctx, bar);
        Assert.Contains(Think, bar);
        Assert.Contains(TurnIn, bar);
    }

    [Fact]
    public void ContextOutlivesBranchAndThinking()
    {
        // 关键保证：窄屏下先丢的是冷门段，ctx 还在
        var bar = Build(60);
        Assert.Contains("ctx ", bar);
    }

    [Fact]
    public void ContextOutlivesTurnTokens()
    {
        var bar = Build(52);
        Assert.Contains("ctx ", bar);
    }

    [Fact]
    public void PathIsShortenedBeforeContextIsDropped()
    {
        // 目录有独立的缩_shortening阶梯_，应当先用上，而不是先丢 ctx
        var bar = Build(48);
        Assert.Contains("ctx ", bar);
        Assert.Contains("…", bar); // 目录被缩短时带省略号，不留「像完整路径的残串」
    }

    [Fact]
    public void ThinkingIsDroppedFirst()
    {
        var bar = Build(100);
        Assert.DoesNotContain("think:", bar);
    }

    [Fact]
    public void HeadIsNeverDropped()
    {
        foreach (var width in new[] { 20, 30, 40, 60, 80 })
        {
            var bar = Build(width);
            Assert.Contains("⏵", bar);
        }
    }

    [Fact]
    public void NeverExceedsTheWidthOnceHeadFits()
    {
        for (var width = 20; width <= 120; width++)
        {
            var bar = Build(width);
            // 极窄时只能硬截；只要行首（模式+模型）还放得下，整行就不该更宽
            var headWidth = TextUtil.DisplayWidth($"⏵ {Mode} · {Model}");
            if (headWidth + 1 > width)
                continue;
            Assert.True(TextUtil.DisplayWidth(bar) <= width,
                $"宽度 {width} 实际 {TextUtil.DisplayWidth(bar)}：{bar}");
        }
    }

    [Fact]
    public void UnknownWidthKeepsEverything()
    {
        var bar = Build(0);
        Assert.Contains(Ctx, bar);
        Assert.Contains(Think, bar);
    }

    [Fact]
    public void NoBranchMeansNoBranchSegment()
    {
        var bar = Build(200, branch: null);
        Assert.DoesNotContain("feature/some-long-branch-name", bar);
    }

    [Fact]
    public void EmptyThinkIsNeverRendered()
    {
        var bar = Build(200, think: "");
        Assert.DoesNotContain("think:", bar);
    }
}
