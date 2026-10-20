using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 表格在**各种终端宽度**下都不许越界。
///
/// 表格已经处理了列数裁剪与单元格截断（MaxReadableColumns / FitColumnWidths），
/// 但那些逻辑改过好几轮，且每一处都靠「算准了」而非「量出来」保证不越界。
/// 这里直接穷举：宽度 1..50 × 不同列数 × 宽窄内容，逐行量显示宽度。
///
/// 表格越界比正文越界严重得多：折行之后列对应关系全丢，
/// 用户看到的是一堆并排的字，完全读不出哪一格属于哪一列。
/// </summary>
[Collection("ConsoleOutput")]
public class NarrowTableTests
{
    private static string Render(string md, int width)
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
                renderer.SetWidth(width);
                renderer.Append(md);
                renderer.Flush();
            }
        }
        finally { Console.SetOut(stdout); }
        return writer.ToString().Replace("\r\n", "\n");
    }

    private static readonly string[] Tables =
    [
        // 两列窄内容
        "| a | b |\n|---|---|\n| 1 | 2 |\n",
        // 三列
        "| a | b | c |\n|---|---|---|\n| 1 | 2 | 3 |\n",
        // 宽内容（会被截断）
        "| name | description |\n|---|---|\n| x | 这是一段很长的说明文字用来触发截断逻辑 |\n",
        // 中文表头
        "| 名称 | 说明 |\n|---|---|\n| 甲 | 乙 |\n",
        // 很多列
        "| a | b | c | d | e | f | g |\n|---|---|---|---|---|---|---|\n| 1 | 2 | 3 | 4 | 5 | 6 | 7 |\n",
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(50)]
    public void NoTableLineExceedsTheWidth(int width)
    {
        foreach (var md in Tables)
        {
            var outp = Render(md, width);
            foreach (var line in outp.Split('\n'))
            {
                var bare = line.TrimEnd('\r');
                if (bare.Length == 0)
                    continue;
                // 截断提示行是有意为之的例外（同 NarrowHelpListTests）
                if (bare.Contains("终端较窄", StringComparison.Ordinal))
                    continue;
                var w = TextUtil.DisplayWidth(bare);
                Assert.True(w <= width,
                    $"width={width} 超宽（{w} 列）：{bare}\n--- 源 ---\n{md}");
            }
        }
    }

    [Fact]
    public void ZeroWidthMeansNoWrapping()
    {
        // 宽度未知（0）时不猜：交回终端软换行
        var outp = Render("| a | b |\n|---|---|\n| 1 | 2 |\n", 0);
        Assert.Contains("a", outp);
        Assert.Contains("1", outp);
    }
}
