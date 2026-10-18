using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 表格外框：把「表头 / 数据」的边界**画出来**，而不是只靠竖线分隔。
///
/// 这里锁的三件事，任何一条失守框线就散架：
/// 1. 形制——顶边/接头/底边用的是对应的角与接头，不能一律用同一个字符；
/// 2. 对齐——框线的接头必须正对内部分隔竖线，数据行的框线与内容不能错位；
/// 3. 宽度——外框自带 4 列成本，窄屏下必须先从列宽里扣掉，否则整行折行、框全断。
/// </summary>
[Collection("ConsoleOutput")]
public class TableFrameTests : IDisposable
{
    private readonly StringWriter _out = new();
    private readonly TextWriter _stdout;

    public TableFrameTests()
    {
        _stdout = Console.Out;
        Console.SetOut(_out);
    }

    public void Dispose() => Console.SetOut(_stdout);

    private string Render(string markdown, int width = 0)
    {
        var r = new ConsoleRenderer(enabled: true);
        if (width > 0)
            r.SetWidth(width);
        r.Append(markdown);
        r.Flush();
        return _out.ToString().Replace("\r\n", "\n");
    }

    private static string[] Lines(string output) =>
        output.Split('\n').Where(l => l.Trim().Length > 0).ToArray();

    [Fact]
    public void TableIsWrappedInAFullFrame()
    {
        var lines = Lines(Render("| a | b |\n|---|---|\n| 1 | 2 |\n"));
        // 顶边 / 表头 / 接头 / 数据 / 底边
        Assert.Equal(5, lines.Length);
        Assert.Contains("┌", lines[0]);
        Assert.Contains("┬", lines[0]);
        Assert.Contains("┐", lines[0]);
        Assert.Contains("├", lines[2]);
        Assert.Contains("┼", lines[2]);
        Assert.Contains("┤", lines[2]);
        Assert.Contains("└", lines[^1]);
        Assert.Contains("┴", lines[^1]);
        Assert.Contains("┘", lines[^1]);
    }

    [Fact]
    public void DataRowsAreClosedOnBothSides()
    {
        var lines = Lines(Render("| a | b |\n|---|---|\n| 1 | 2 |\n"));
        foreach (var line in new[] { lines[1], lines[3] })
        {
            Assert.StartsWith("  │", line);   // 左边框
            Assert.EndsWith("│", line);        // 右边框
            Assert.Equal(3, line.Split("│").Length - 1); // 外框 2 + 内分隔 1
        }
    }

    [Fact]
    public void FrameJunctionsLineUpWithColumnSeparators()
    {
        // 框线歪掉比不画框更糟：接头错位会让人以为列边界就在错位处
        var lines = Lines(Render("| alpha | beta |\n|---|---|\n| 1 | 2 |\n"));
        var top = lines[0];
        var header = lines[1];
        // 第一条内部分隔竖线（跳过外框那条）的列位置
        var headerInner = IndexOfNth(header, '│', 2);
        var topJoint = top.IndexOf('┬');
        Assert.True(headerInner > 0 && topJoint > 0, $"找不到接头: [{top}] / [{header}]");
        Assert.Equal(headerInner, topJoint);
    }

    private static int IndexOfNth(string s, char c, int n)
    {
        var seen = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != c)
                continue;
            if (++seen == n)
                return i;
        }
        return -1;
    }

    [Fact]
    public void EveryLineFitsTheTerminalWidth()
    {
        // 外框多占 4 列：预算里不扣掉，这条就会超宽。
        //
        // 必须用**单列**宽表：多列表格一超宽就会先被 MaxReadableColumns 裁掉一列，
        // 宽度反而又降回去了——那种用例证明不了"列宽收敛是否算进了外框"。
        // 单列表格裁无可裁（至少保留一列），所以宽度只能靠 FitColumnWidths 收：
        //   自然宽 36 列 + 缩进 2 = 38 ≤ 40（不超）
        //   加外框 4 列后 = 42 > 40（必须削到 34 才放得下）
        const int width = 40;
        var wide = new string('x', 36);
        var lines = Lines(Render($"| k |\n|---|\n| {wide} |\n", width));
        foreach (var line in lines)
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"超出 {width} 列: [{line}] 宽 {TextUtil.DisplayWidth(line)}");
    }

    [Fact]
    public void WideCellIsActuallyShrunkNotLeftUnconstrained()
    {
        // 上一条的反向证伪：确认内容真的被削了，而不是"正好没人超宽"。
        // 单列表 + 超长内容 → 单元格必须短于原文，且带省略号。
        const int width = 40;
        var wide = new string('x', 36);
        var output = Render($"| k |\n|---|\n| {wide} |\n", width);
        Assert.DoesNotContain(wide, output); // 36 个 x 原样出现就等于没削
        Assert.Contains(SafeColor.Glyphs.Ellipsis, output);
    }

    [Fact]
    public void AsciiModeDrawsTheFrameWithPlusAndDash()
    {
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = n => n == "CODEAGENT_ASCII" ? "1" : null;
            var lines = Lines(Render("| a | b |\n|---|---|\n| 1 | 2 |\n"));
            Assert.Contains("+", lines[0]);
            Assert.Contains("-", lines[0]);
            // 制表符一个都不能漏出去
            foreach (var line in lines)
                Assert.DoesNotContain(line, c => c is '┌' or '┬' or '┐' or '├' or '┼' or '┤' or '└' or '┴' or '┘');
        }
        finally { SafeColor.ReadEnv = saved; }
    }

    [Fact]
    public void SubstituteCoversTheWholeFrameFamily()
    {
        // 那些先把整行拼好、再统一退回 ASCII 的调用点靠的是 Substitute：
        // 框线字形漏在里面就会留下一行 ? 。
        var saved = SafeColor.ReadEnv;
        try
        {
            const string frame = "┌┬┐├┼┤└┴┘";
            SafeColor.ReadEnv = _ => null;
            Assert.Equal(frame, SafeColor.Glyphs.Substitute(frame)); // 未退回时原样
            SafeColor.ReadEnv = n => n == "CODEAGENT_ASCII" ? "1" : null;
            // 九个框线字符全部落到 `+`，一个都不能漏
            Assert.All(SafeColor.Glyphs.Substitute(frame), c => Assert.Equal('+', c));
        }
        finally { SafeColor.ReadEnv = saved; }
    }
}
