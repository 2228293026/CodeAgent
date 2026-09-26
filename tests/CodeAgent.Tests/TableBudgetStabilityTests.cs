using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 表格预算的宽度读数抖动。
/// 回归点：<c>TableBudget()</c> 此前直接拿 <c>Console.WindowWidth</c> 当预算。
/// 终端最小化、拖拽过程中的抖动都会让它**短暂返回 1~2 列**，于是每一列都被压到
/// 1~2 字符（`a │ b │ c`）——这正是 <see cref="Program.MinPlausibleColumns"/>
/// 当初为了防止「一次测量失败就让后续输出退化」而引入的，却没被套用到表格上。
/// 现在沿用同一套「测量值 + 上一次好值」的解析。
/// </summary>
[Collection("ConsoleOutput")]
public class TableBudgetStabilityTests
{
    private static string RenderTable(int width, string markdown)
    {
        var writer = new System.IO.StringWriter();
        var stdout = Console.Out;
        try
        {
            Console.SetOut(writer);
            var renderer = new ConsoleRenderer(true);
            renderer.SetWidth(width);
            renderer.Append(markdown);
            renderer.Flush();
        }
        finally { Console.SetOut(stdout); }
        return writer.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void ImplausibleWidthIsRejectedByTheSharedRule()
    {
        // 这条规则本身由 Program 维护；此处钉住「表格用的是同一套规则」
        Assert.False(Program.ResolveColumns(1, 80) < 80);
        Assert.False(Program.ResolveColumns(2, 80) < 80);
        Assert.Equal(80, Program.ResolveColumns(1, 80));
        Assert.Equal(80, Program.ResolveColumns(2, 80));
    }

    [Fact]
    public void UnknownWidthStillMeansUnbounded()
    {
        // 宽度未知时返回 0 = 不限，这是既有约定，不能被「上一次好值」改掉
        Assert.Equal(0, Program.ResolveColumns(0, 0));
    }

    [Fact]
    public void PlausibleMeasurementIsUsedAsIs()
    {
        Assert.Equal(120, Program.ResolveColumns(120, 80));
    }

    [Fact]
    public void ExplicitWidthStillWins()
    {
        // 显式 SetWidth 的路径不受窗口读数影响
        var output = RenderTable(60, "| a | b |\n| --- | --- |\n| 1 | 2 |\n");
        Assert.Contains("a", output);
    }

    [Fact]
    public void TableDoesNotCollapseOnNarrowWidth()
    {
        // 窄到放不下时的行为是丢列并说明，不是把每列压成 1 字符
        var output = RenderTable(16, "| name | value |\n| --- | --- |\n| alpha | 1 |\n");
        foreach (var line in ConsoleRendererTests.OutputLines(output).Where(l => l.Trim().Length > 0))
            Assert.True(TextUtil.DisplayWidth(line) <= 16, line);
    }

    [Fact]
    public void BudgetSourceIsTheSharedResolver()
    {
        // 源码级守卫：TableBudget 必须走 ResolveColumns，不能再直接用 WindowWidth 当预算
        var source = System.IO.File.ReadAllText(FindRenderer());
        var at = source.IndexOf("private int TableBudget()", StringComparison.Ordinal);
        Assert.True(at >= 0, "找不到 TableBudget");
        var body = source[at..];
        body = body[..body.IndexOf("_lastGoodWidth;", StringComparison.Ordinal)];
        Assert.Contains("Program.ResolveColumns", body);
        Assert.False(body.Contains("return Math.Clamp(Console.WindowWidth"), body);
    }

    private static string FindRenderer()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = System.IO.Path.Combine(dir, "src", "CodeAgent", "ConsoleRenderer.cs");
                if (System.IO.File.Exists(candidate) && new System.IO.FileInfo(candidate).Length > 1000)
                    return candidate;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/ConsoleRenderer.cs");
    }
}
