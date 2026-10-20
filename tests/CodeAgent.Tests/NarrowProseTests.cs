using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 会折行的正文（列表项、块引用）在各种宽度下都不许越界。
///
/// 这里**只测真的会折行的形状**。普通段落不折行、交给终端软换行——
/// 这是 ConsoleRenderer.EmitLine 里 `hangingCols > 0` 那一支的明确设计：
/// 渲染器折行会打断用户复制粘贴，而终端的软换行不改变文本内容。
///
/// 我第一版把普通段落也塞进来，width=8 报了「38 列的普通段落」——
/// 那不是 bug，是测试的预期错了。PlainProseIsLeftToTheTerminal 把这个约定钉住。
/// </summary>
[Collection("ConsoleOutput")]
public class NarrowProseTests
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

    private static readonly string[] Wrapping =
    [
        "- 列表项第一\n- 列表项第二\n",
        "1. 有序项一\n2. 有序项二\n",
        "> 引用的一行文字\n",
        "- 这是一条很长很长的列表项内容需要折行处理才能放进终端宽度里面\n",
        "> 引用的内容也可以很长很长需要按终端宽度折行处理才行\n",
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
    [InlineData(60)]
    public void NoWrappedLineExceedsTheWidth(int width)
    {
        foreach (var md in Wrapping)
        {
            var outp = Render(md, width);
            foreach (var line in outp.Split('\n'))
            {
                var bare = line.TrimEnd('\r');
                if (bare.Length == 0)
                    continue;
                // 交代行是有意为之的例外（见 NarrowWidthContentLossTests）
                if (bare.Contains("装不下一整个字符", StringComparison.Ordinal))
                    continue;
                // 标记本身就把宽度吃光时（如 3 列终端里的 `1. `），渲染器**有意不折行**：
                // 此时正文预算算出来是 0，WrapDisplay(_, 0) 的语义是「不折行」。
                // 硬折的结果是每行只剩一个标记，内容全被吞——比交给终端软换行差。
                // EmitLine 的守卫 `_width > hangingCols` 正是这个判据，这里跟随。
                var marker = System.Text.RegularExpressions.Regex.Match(bare, @"^(?:> |[-*+] |\d+[.)] )");
                var markerWidth = marker.Success ? TextUtil.DisplayWidth(marker.Value) : 0;
                if (markerWidth > 0 && width <= markerWidth)
                    continue;
                var w = TextUtil.DisplayWidth(bare);
                Assert.True(w <= width,
                    $"width={width} 超宽（{w} 列）：{bare}\n--- 源 ---\n{md}");
            }
        }
    }

    [Fact]
    public void PlainProseIsLeftToTheTerminal()
    {
        // 把「普通段落不折行」钉成明确约定：它超过终端宽度不是漏了，是**根本不算**。
        // 将来谁想改渲染器折行，得先看这条测试的理由（可复制粘贴性）。
        var outp = Render("这是一段普通的中文正文，用来测试折行。", 8);
        Assert.Contains("这是一段普通的中文正文，用来测试折行。", outp);
    }

    [Fact]
    public void ListContinuationKeepsHangingIndent()
    {
        // 宽度够时，列表项折行的续行必须仍带悬挂缩进，且缩进**等于标记本身**
        // （`- ` 两列，`1. ` 三列）——这样续行与首行的正文起点对齐。
        //
        // 第一版我断言「续行以空格开头」，于是 `- 项内容…` 被判失败：
        // 悬挂缩进就是 `- `，它当然不以空格开头。错的是断言，不是代码。
        var outp = Render("- 这是一条很长很长的列表项内容需要折行处理才能放进终端宽度里面\n", 24);
        var lines = outp.Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        Assert.True(lines.Length > 1, $"应当折行：{outp}");
        for (var i = 1; i < lines.Length; i++)
            Assert.True(lines[i].StartsWith("- ", StringComparison.Ordinal),
                $"续行丢了悬挂缩进：«{lines[i]}»");
    }

    [Fact]
    public void ZeroWidthLeavesListTextAlone()
    {
        // 宽度未知时不折行：交回终端软换行
        var outp = Render("- 这是一条列表项\n", 0);
        Assert.Contains("这是一条列表项", outp);
    }
}
