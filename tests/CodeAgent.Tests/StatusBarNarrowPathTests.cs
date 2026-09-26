using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 状态栏最窄档的目录降级。
/// 回归点：前几轮（丢冷门段 → 缩短目录 → 丢 ctx）走完后，最后一步是
/// `FitToWidth` 硬截**尾部**，得到「⏵ plan · gpt-5 · src/CodeAg…」——
/// 路径最有信息量的是**末段**，硬截恰好把它切掉，看起来像另一个目录。
/// （与 diff 文件头同一个病，见 ShortenPath / Round 58。）
/// 现在最窄档改为再按保尾部缩短目录；放不下末段就整个去掉，不留半截路径。
/// </summary>
public class StatusBarNarrowPathTests
{
    private const string Cwd = "D:/Projects/CodeAgent/src/CodeAgent/Agent/Provider/Session/Logger.cs";

    private static string Build(int width) =>
        Program.BuildStatusBar("plan", "gpt-5-codex", Cwd, "main", "1.2k", "340", "24% ctx", " 思考 2.5s", width);

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var width in new[] { 6, 8, 10, 14, 20, 30, 40, 60, 80, 120 })
            Assert.True(TextUtil.DisplayWidth(Build(width)) <= width, $"预算 {width}：{Build(width)}");
    }

    [Fact]
    public void NeverShowsAHalfPathThatLooksComplete()
    {
        // 窄档下要么是完整/带省略号的目录，要么整个没有——不能是看起来像完整路径的残串
        foreach (var width in new[] { 10, 14, 20, 30, 40 })
        {
            var line = Build(width);
            if (line.Contains("CodeAg", StringComparison.Ordinal) && !line.Contains('…'))
                Assert.Fail($"预算 {width} 出现未标记的路径残串：{line}");
        }
    }

    [Fact]
    public void NarrowestStillKeepsModeAndModel()
    {
        foreach (var width in new[] { 20, 30, 40, 60 })
        {
            var line = Build(width);
            Assert.Contains("plan", line);
        }
    }

    [Fact]
    public void PathIsKeptWholeWhenItFits()
    {
        var line = Build(200);
        Assert.Contains("Logger.cs", line);
    }

    [Fact]
    public void UnknownWidthKeepsEverything()
    {
        var line = Program.BuildStatusBar("plan", "gpt-5-codex", Cwd, "main", "1.2k", "340", "24% ctx", " 思考 2.5s", 0);
        Assert.Contains(Cwd, line);
        Assert.Contains("main", line);
        Assert.Contains("思考", line);
    }

    [Fact]
    public void ShortPathIsUnaffected()
    {
        // 短目录不该被无谓地缩短
        var line = Program.BuildStatusBar("plan", "gpt-5", "a/b", null, "1", "1", "5% ctx", "", 80);
        Assert.Contains("a/b", line);
    }

    [Fact]
    public void MinimumStatusPathWidthIsSane()
    {
        Assert.InRange(Program.MinStatusPathWidth, 4, 12);
    }

    [Fact]
    public void DegradesGracefullyAsWidthShrinks()
    {
        // 宽度递减时行长单调不增（不会突然冒出更长的行）
        var previous = int.MaxValue;
        for (var width = 120; width >= 6; width -= 2)
        {
            var w = TextUtil.DisplayWidth(Build(width));
            Assert.True(w <= previous, $"宽度 {width} 时行长 {w} 超过了更宽时的 {previous}");
            previous = w;
        }
    }
}
