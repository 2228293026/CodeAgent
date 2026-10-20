using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 表格单元格里的竖线。
///
/// 两种来源：
/// ① <c>\|</c> 转义——SplitCells 处理了（奇偶反斜杠判定）；
/// ② **行内代码里的 |**——Markdown 规范规定代码段内的内容按字面处理，
///    所以 `a | b` 里的竖线是内容，不是列分隔符。
///
/// ②很容易漏：切分发生在行内代码解析**之前**，那时还不知道这一段在反引号里。
/// 漏了的表现是——用户写 `` `a | b` `` 想要一格，得到两格，
/// 而错位没有任何提示。
/// </summary>
[Collection("ConsoleOutput")]
public class TablePipeTests
{
    private static string Render(string md)
    {
        var scope = ColourTestScope.Off();
        var writer = new StringWriter();
        var stdout = Console.Out;
        try
        {
            using (scope)
            {
                Console.SetOut(writer);
                var renderer = new ConsoleRenderer(true);
                renderer.SetWidth(100);
                renderer.Append(md);
                renderer.Flush();
            }
        }
        finally { Console.SetOut(stdout); }
        return writer.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void EscapedPipeStaysInOneCell()
    {
        var outp = Render("| a | b |\n|---|---|\n| x\\|y | z |\n");
        Assert.Contains("x|y", outp);
        // 两列：渲染用的是 │（ColumnSeparator）不是 ASCII 的 |，
        // 2 列表格 = 外框 2 个 + 中间分隔 1 个 = 3 个竖线。
        // 第一版断言数的是 '|'，一个都数不到（渲染出来是 │），于是 0-1 != 2 必失败。
        var dataLine = outp.Split('\n').First(l => l.Contains("x|y"));
        Assert.Equal(3, dataLine.Count(c => c == '│'));
    }

    [Fact]
    public void PipeInsideInlineCodeIsLiteral()
    {
        // Markdown 规范：代码段内按字面处理，`a | b` 是一个格子的内容
        var outp = Render("| 表达式 | 说明 |\n|---|---|\n| `a | b` | 位或 |\n");
        // 这一行仍应是 2 列（不是 3 列），且 `a | b` 完整出现在同一格里
        Assert.Contains("a", outp);
        Assert.Contains("b", outp);
        var dataLine = outp.Split('\n').First(l => l.Contains("位或"));
        // 数渲染用的框线 │（不是 ASCII 的 |）：2 列表格 = 外框 2 个 + 中间分隔 1 个 = 3。
        // 第一版这里数的也是 '|'，而那一行恰好只有一个（行内代码里的那个字面竖线），
        // 于是断言失败但**渲染其实已经正确**——差点据此以为没修好。
        Assert.Equal(3, dataLine.Count(c => c == '│'));
    }

    [Fact]
    public void ThreeColumnsStillWork()
    {
        var outp = Render("| a | b | c |\n|---|---|---|\n| 1 | 2 | 3 |\n");
        var dataLine = outp.Split('\n').First(l => l.Contains('1') && l.Contains('3'));
        // 3 列 = 外框 2 + 中间分隔 2 = 4 个竖线
        Assert.Equal(4, dataLine.Count(c => c == '│'));
    }
}
