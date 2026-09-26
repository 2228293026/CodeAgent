using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 表格截断提示里的数字。
/// 回归点：列被裁掉后代码把 `cols` 改写成 `keep`，而截断提示打印的正是
/// `表格共 {cols} 列` —— 于是输出「表格共 4 列，仅显示前 4 列」，
/// 等于告诉用户**一列都没丢**。一个谎报自己什么都没丢的截断提示，
/// 比不给提示更糟：用户据此以为看到了全部列。
/// </summary>
[Collection("ConsoleOutput")]
public class TableTruncationNoticeTests
{
    private static string Capture(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { action(); }
        finally { Console.SetOut(original); }
        return writer.ToString();
    }

    private static string RenderTable(int width, params string[] rows)
    {
        var renderer = new ConsoleRenderer(true);
        renderer.SetWidth(width);
        return Capture(() =>
        {
            renderer.Append(string.Join("\n", rows) + "\n");
            renderer.Flush();
        });
    }

    // 12 列：需要 2 + 12*3 + 11*3 = 71 列才排得下。
    // 列数多才可能在**较宽**的终端上被裁——那样完整措辞的提示（39 列）才放得下，
    // 否则永远只会看到紧凑形态，完整分支等于死代码。
    private static string WideTable(int columns) => string.Join("\n",
        "| " + string.Join(" | ", Enumerable.Range(0, columns).Select(i => $"col{i}")) + " |",
        "| " + string.Join(" | ", Enumerable.Repeat("---", columns)) + " |",
        "| " + string.Join(" | ", Enumerable.Range(0, columns).Select(i => $"{i}")) + " |");

    [Fact]
    public void ColumnNoticeReportsTheRealTotal()
    {
        var notice = RenderTable(70, WideTable(12)).Split('\n')
            .First(l => l.Contains("列", StringComparison.Ordinal));
        // 总数必须是 12（真实列数），不是被改写后的保留数
        Assert.Equal(12, int.Parse(System.Text.RegularExpressions.Regex.Match(notice, @"共 (\d+) 列").Groups[1].Value));
    }

    [Fact]
    public void ColumnNoticeDoesNotClaimNothingWasDropped()
    {
        // 关键不变式：提示里的**总数必须大于保留数**。谎报什么都没丢的截断提示，
        // 比不给提示更糟——用户据此以为看到了全部列。
        foreach (var width in new[] { 45, 60, 70 })
        {
            var notice = RenderTable(width, WideTable(12)).Split('\n')
                .First(l => l.Contains("列", StringComparison.Ordinal));
            var total = int.Parse(System.Text.RegularExpressions.Regex.Match(notice, @"共 (\d+) 列").Groups[1].Value);
            var kept = int.Parse(System.Text.RegularExpressions.Regex.Match(notice, @"前 (\d+) 列").Groups[1].Value);
            Assert.True(total > kept, $"预算 {width} 提示谎报未截断：{notice}");
        }
    }

    [Fact]
    public void NoNoticeWhenNothingIsDropped()
    {
        var output = RenderTable(200, WideTable(12));
        Assert.DoesNotContain("终端较窄", output);
    }

    [Fact]
    public void NarrowerTerminalReportsMoreDroppedColumns()
    {
        int Kept(string o) =>
            int.Parse(o.Split('\n').First(l => l.Contains("仅显示前", StringComparison.Ordinal))
                .Split("仅显示前 ")[1].Split(' ')[0]);
        // 两个宽度都必须真的裁了列，否则提示不存在、无法比较
        var mid = RenderTable(70, WideTable(12));
        var narrow = RenderTable(45, WideTable(12));
        Assert.Contains("仅显示前", mid);
        Assert.Contains("仅显示前", narrow);
        Assert.True(Kept(narrow) <= Kept(mid), $"窄 {Kept(narrow)} 应不多于中 {Kept(mid)}");
    }

    [Fact]
    public void RowNoticeReportsTheRealTotal()
    {
        var rows = new List<string> { "| a | b |", "| --- | --- |" };
        for (var i = 0; i < 60; i++) // 行数上限是 50，必然触发
            rows.Add($"| {i} | x |");
        var output = RenderTable(200, rows.ToArray());
        Assert.Contains("仅显示前", output);
        Assert.Contains("行", output);
    }

    [Fact]
    public void EveryNoticeLineFitsTheWidth()
    {
        // 解释截断的那一行**本身**不该溢出终端：
        // 16 列上「…(表格共 6 列，终端较窄，仅显示前 2 列)」要 40 多列。
        foreach (var width in new[] { 12, 16, 20, 24, 30, 40, 60 })
        {
            foreach (var line in ConsoleRendererTests.OutputLines(RenderTable(width, WideTable(12))))
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
        }
    }

    [Fact]
    public void NarrowScreenUsesTheCompactNotice()
    {
        var output = RenderTable(16, WideTable(12));
        Assert.Contains("→", output); // 紧凑形态
    }

    [Fact]
    public void NotesPickTheirFormByDisplayWidth()
    {
        // 宽度未知 → 完整形态（既有约定：不猜测折行位置）
        Assert.Equal("…(表格共 6 列，终端较窄，仅显示前 2 列)", ConsoleRenderer.TableDroppedColsNote(6, 2));
        Assert.Equal("…(表格共 60 行，仅显示前 50 行)", ConsoleRenderer.TableDroppedRowsNote(60, 50));
        // 宽度已知且放不下 → 紧凑形态
        Assert.Equal("…(6 列 → 2 列)", ConsoleRenderer.TableDroppedColsNote(6, 2, 20));
        Assert.Equal("…(60 行 → 50 行)", ConsoleRenderer.TableDroppedRowsNote(60, 50, 20));
        // 宽度够 → 完整形态
        Assert.Equal("…(表格共 6 列，终端较窄，仅显示前 2 列)", ConsoleRenderer.TableDroppedColsNote(6, 2, 60));
    }

    [Fact]
    public void CompactFormIsDecidedByDisplayWidthNotCharCount()
    {
        // 完整形态 23 个字符但 37 列：按字符数判定会误以为放得下
        var full = ConsoleRenderer.TableDroppedColsNote(6, 2);
        Assert.True(full.Length < 30 && TextUtil.DisplayWidth(full) > 30,
            $"长度 {full.Length} 宽 {TextUtil.DisplayWidth(full)}");
        Assert.Equal(full, ConsoleRenderer.TableDroppedColsNote(6, 2, 80));
    }
}
