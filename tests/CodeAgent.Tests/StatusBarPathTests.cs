using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 状态栏在极端窄屏下的降级。
/// 回归点：可丢段（思考/token/上下文/分支）丢完后是直接按显示宽度硬截，
/// 会留下「看起来是完整路径的残串」——用户会以为那就是真实目录，比截断本身更危险。
/// </summary>
public class StatusBarPathTests
{
    [Fact]
    public void ShortenPath_FitsPathReturnsUnchanged()
    {
        var path = @"C:\a\b";
        Assert.Equal(path, Program.ShortenPath(path, 40));
    }

    [Fact]
    public void ShortenPath_KeepsTailSegments()
    {
        var path = @"C:\Users\very\long\name\Projects\CodeAgent\src\CodeAgent";
        var shortened = Program.ShortenPath(path, 24);
        Assert.StartsWith("…\\", shortened);
        Assert.EndsWith("CodeAgent", shortened);
        Assert.DoesNotContain("Users", shortened);
        Assert.True(TextUtil.DisplayWidth(shortened) <= 24, $"超宽: {shortened}");
    }

    [Fact]
    public void ShortenPath_UsesForwardSlashOnUnixPaths()
    {
        var shortened = Program.ShortenPath("/home/user/projects/CodeAgent/src", 20);
        Assert.StartsWith("…/", shortened);
    }

    [Fact]
    public void ShortenPath_AlwaysMarksTruncation()
    {
        // 单段超长目录名：也必须带 … 前缀，不能返回无标记残串
        var shortened = Program.ShortenPath(new string('x', 200), 20);
        Assert.StartsWith("…", shortened);
        Assert.True(TextUtil.DisplayWidth(shortened) <= 20, $"超宽: {shortened}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(20)]
    public void ShortenPath_NeverExceedsBudget(int budget)
    {
        var path = @"C:\Users\very\long\name\Projects\CodeAgent";
        Assert.True(TextUtil.DisplayWidth(Program.ShortenPath(path, budget)) <= budget, $"预算 {budget} 溢出");
    }

    [Fact]
    public void ShortenPath_NoBudgetReturnsUnchanged()
    {
        // budget <= 0 = 不请求缩短，保持原样
        var path = @"C:\Users\very\long\name\Projects\CodeAgent";
        Assert.Equal(path, Program.ShortenPath(path, 0));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void BuildStatusBar_NeverExceedsWidth(int width)
    {
        var text = Program.BuildStatusBar(
            "code", "gpt-5", @"C:\Users\very\long\name\Projects\CodeAgent\src\CodeAgent",
            "feature/长分支名", "12.3K", "4.5K", "ctx 183.2K/200K (92%)", "think:high", width);
        Assert.True(TextUtil.DisplayWidth(text) <= width, $"宽度 {width} 溢出: {text}");
    }

    [Fact]
    public void BuildStatusBar_KeepsModeAndModelWhenPathIsShortened()
    {
        var text = Program.BuildStatusBar(
            "code", "gpt-5", @"C:\Users\very\long\name\Projects\CodeAgent", null, "0", "0", "", "", 40);
        Assert.StartsWith("⏵ code · gpt-5", text);
        // 目录被缩短并带标记，而不是留下无标记的残串
        Assert.Contains("…", text);
    }

    [Fact]
    public void BuildStatusBar_UnknownWidthKeepsEverything()
    {
        var text = Program.BuildStatusBar(
            "code", "gpt-5", @"C:\a\b", "main", "1", "2", "ctx", "think", 0);
        Assert.Contains("main", text);
        Assert.Contains("think", text);
    }
}
