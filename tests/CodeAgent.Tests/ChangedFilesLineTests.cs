using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 回合结束后的「本轮改了哪些文件」一行（学自 Claude Code 的 Modified files）。
/// </summary>
public sealed class ChangedFilesLineTests
{
    private static List<string> L(params string[] s) => s.ToList();

    // —— 计算本轮新增 ——

    [Fact]
    public void New_IsSetDifferenceNotCountDifference()
    {
        // AllPaths 已去重。同一文件在一轮里被改两次：数量差 = 1，集合差 = 0。
        // 按数量差算会报"改了 1 个文件"——而它其实上一轮就改过了，
        // 用户要的恰恰是"这一轮新动了什么"。
        Assert.Empty(Program.NewFilesThisTurn(L("a.cs"), L("a.cs", "a.cs")));
        // 真正的新文件照样要报出来
        Assert.Equal(L("c.cs"), Program.NewFilesThisTurn(L("a.cs", "b.cs"), L("c.cs", "a.cs", "b.cs")));
    }

    [Fact]
    public void New_EmptyWhenNothingChanged()
    {
        Assert.Empty(Program.NewFilesThisTurn(L("a.cs"), L("a.cs")));
        Assert.Empty(Program.NewFilesThisTurn(Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void New_HandlesReordering()
    {
        // AllPaths 是"最近优先"，顺序会变；集合差不该受影响
        Assert.Equal(L("c.cs"), Program.NewFilesThisTurn(L("a.cs", "b.cs"), L("c.cs", "b.cs", "a.cs")));
    }

    // —— 渲染 ——

    [Fact]
    public void Line_EmptyWhenNoFiles()
    {
        Assert.Equal(string.Empty, Program.FormatChangedFilesLine(Array.Empty<string>(), 80));
    }

    [Fact]
    public void Line_ListsFilesWhenTheyFit()
    {
        var line = Program.FormatChangedFilesLine(L("a.cs", "b.cs"), 80);
        Assert.Contains("a.cs", line);
        Assert.Contains("b.cs", line);
        Assert.DoesNotContain("等", line);
    }

    [Fact]
    public void Line_AddsEtcWhenTruncated()
    {
        // "等"是关键的：它告诉用户后面还有，而不是让人以为就改了这两个
        var many = Enumerable.Range(0, 30).Select(i => $"src/very/long/path/File{i}.cs").ToList();
        var line = Program.FormatChangedFilesLine(many, 60);
        Assert.Contains("等 30 个", line);
    }

    [Fact]
    public void Line_NeverExceedsWidth()
    {
        var many = Enumerable.Range(0, 40).Select(i => $"src/quite/a/long/path/File{i}.cs").ToList();
        for (var w = 8; w <= 200; w++)
        {
            var line = Program.FormatChangedFilesLine(many, w);
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"w={w} 超宽 {TextUtil.DisplayWidth(line)}: {line}");
        }
    }

    [Fact]
    public void Line_NeverShowsAHalfPath()
    {
        // 放不下整条路径时宁可整段不显示，也不留半截——半截路径看不出是哪个文件
        var long1 = new string('a', 200) + ".cs";
        Assert.Equal(string.Empty, Program.FormatChangedFilesLine(L(long1), 30));
    }

    [Fact]
    public void Line_ShowsAtLeastOneFileWhenItCan()
    {
        var line = Program.FormatChangedFilesLine(L("a.cs"), 20);
        Assert.Contains("a.cs", line);
    }

    [Fact]
    public void Line_UnknownWidthShowsEverything()
    {
        var line = Program.FormatChangedFilesLine(L("a.cs", "b.cs"), 0);
        Assert.Contains("a.cs", line);
        Assert.Contains("b.cs", line);
    }

    [Fact]
    public void Line_CjkPathsCountByDisplayWidth()
    {
        // 中文文件名按字符数算会低估近一倍，窄终端下立刻超宽
        var files = new[] { "文档/说明/第一章.md", "文档/说明/第二章.md" };
        for (var w = 10; w <= 80; w++)
        {
            var line = Program.FormatChangedFilesLine(files, w);
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"w={w} 超宽: {line}");
        }
    }

    [Fact]
    public void Line_IsPrintedAfterTheSummaryNotInside()
    {
        // 独立成行：BuildTurnSummary 的丢段优先级调了很久，塞新段会搅乱它
        var summary = Program.BuildTurnSummary(3, 12, "12.3s", "↑1.2k ↓340", "", "", "", 120);
        Assert.DoesNotContain("改", summary);
    }

    [Fact]
    public void Line_IsWiredIntoTheTurnLoop()
    {
        // 只测纯函数会漏掉「接进回合循环」这一步
        var src = System.IO.File.ReadAllText(FindProgram());
        Assert.Contains("NewFilesThisTurn(", src);
        Assert.Contains("FormatChangedFilesLine(", src);
    }

    private static string FindProgram()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
