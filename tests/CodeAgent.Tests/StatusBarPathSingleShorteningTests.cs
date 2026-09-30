using System;
using System.IO;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 状态栏的路径只应被**缩短一次**。
///
/// <c>PrintStatusBar</c> 此前先用 <c>TruncatePathHead</c> 按固定 42 列截一刀，
/// <c>BuildStatusBar</c> 又按自己算出的 <c>pathBudget</c> 缩短一次——
/// 二次缩短作用在一个**已经残缺**的串上，两个不同的省略号会叠在一起。
/// 真机 60 列下渲染成 <c>...…a...</c>：ShortenPath 的 "..." 夹着 TruncatePathHead
/// 留下的 "…" 和被硬截的末段尾巴。
///
/// 这里**只保留真能区分的断言**：先截再缩与直接缩，在短预算下的输出常常**恰好相同**
/// （两截都落到 <c>…末段</c>）。曾经写过"数一数有几个省略号"的版本——它两边都通过，
/// 等于没有测试，比没有更糟。真正的保证来自下面那条源码级接线断言。
/// </summary>
public sealed class StatusBarPathSingleShorteningTests
{
    private const string DeepPath = @"C:\Users\someone\AppData\Local\Temp\codeagent-d2-12b8a89db00c4bb9bf718cce";

    [Fact]
    public void PrintStatusBarPassesTheUntruncatedPath()
    {
        // 真正能守住这条的断言：路径必须原样交给 BuildStatusBar，由它按整行剩余宽度缩短。
        // 预截断一旦加回来，这条立刻红。
        var src = File.ReadAllText(FindSource());
        Assert.Contains("var shownCwd = Environment.CurrentDirectory;", src);
        Assert.DoesNotContain("var shownCwd = TruncatePathHead(", src);
    }

    [Fact]
    public void TheCommentExplainsWhyThePreTruncationIsWrong()
    {
        // 只写代码不留理由，下一个读到这里的人会觉得"两处都截一下更保险"
        var src = File.ReadAllText(FindSource());
        Assert.Contains("缩短交给 BuildStatusBar", src);
    }

    [Fact]
    public void FooterBarKeepsItsOwnExplicitBudget()
    {
        // 页脚用的是「固定 42 列预算」下的 TruncatePathHead，那里的预截断是**对的**：
        // 页脚不按整行剩余宽度重算。这条把两种用法区分开，别顺手一起改掉。
        var src = File.ReadAllText(FindSource());
        Assert.Contains("TruncatePathHead(Environment.CurrentDirectory, 42)", src);
    }

    [Fact]
    public void FullPathsStillFitWhenThereIsRoom()
    {
        // 不能为了去掉预截断，把"宽终端下显示完整路径"也弄丢
        var bar = Program.BuildStatusBar("code", "m", @"D:\Projects\CodeAgent", "main",
            "0", "0", "ctx 1%", "high", 120);
        Assert.Contains(@"D:\Projects\CodeAgent", bar);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void StatusBarNeverExceedsItsWidth(int width)
    {
        var bar = Program.BuildStatusBar("code", "kilo/stealth/space-bunny-alpha", DeepPath, "main",
            "1.2k", "340", "ctx 24%", "think:high", width);
        Assert.True(TextUtil.DisplayWidth(bar) <= width, $"宽度 {width} 超宽：{bar}");
    }

    [Fact]
    public void TruncationIsAlwaysMarked()
    {
        // 直接按显示宽度硬截会留下「看起来是完整路径的残串」——用户会以为那就是真实目录
        var bar = Program.BuildStatusBar("code", "kilo/stealth/space-bunny-alpha", DeepPath, null,
            "0", "0", "ctx 4%", "think:high", 60);
        var location = LocationOf(bar);
        Assert.True(location.Contains(SafeColor.Glyphs.Ellipsis, StringComparison.Ordinal)
                    || location.EndsWith("..."),
            $"截短必须带省略号标记（实际：{location}）");
    }

    /// <summary>状态栏的段序是「标记+模式 · 模型 · 路径(分支) · …」——路径是**第 3 段**。</summary>
    private static string LocationOf(string bar)
    {
        var parts = bar.Split(SafeColor.Glyphs.SegmentSeparator);
        return parts.Length > 2 ? parts[2] : string.Empty;
    }

    private static string FindSource()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
