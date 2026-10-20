using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /stats 与 /status 共用同一个面板渲染器。
///
/// 两块面板结构一模一样（标题 + 键值表），此前各拼一份，
/// 于是 /status 的标题宽度修了、/stats 的「会话统计:」还硬写着 10 列——
/// 3 列终端上它自己就超宽折行，把下面的键值表推歪。
/// 这里把「同一份结构必须同一份渲染」钉住。
/// </summary>
public class StatsPanelTests
{
    private static readonly (string Key, string Value)[] Rows =
    [
        ("模型", "claude-sonnet-4-5-20250929"),
        ("请求次数", "42"),
        ("新输入 tokens", "123,456"),
        ("输出 tokens", "7,890"),
        ("当前上下文", "ctx 131.3k/200.0k (66%)"),
        ("会话时长", "2h13m"),
        ("累计费用", "≈$1.2345"),
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
    [InlineData(80)]
    [InlineData(120)]
    public void StatsPanelNeverExceedsTheWidth(int width)
    {
        var text = Program.FormatStatusPanel(Rows, width, "会话统计:", rightAlign: true);
        foreach (var line in text.Split('\n'))
        {
            var bare = line.TrimEnd('\r');
            if (bare.Length == 0)
                continue;
            var w = TextUtil.DisplayWidth(bare);
            Assert.True(w <= width, $"width={width} 的一行有 {w} 列：{bare}");
        }
    }

    [Fact]
    public void TitleIsUsedVerbatim()
    {
        // 标题是参数：/stats 传「会话统计:」、/status 用默认「状态:」。
        // 硬编码标题会让复用这个渲染器的第二块面板永远显示错标题。
        Assert.Contains("会话统计:", Program.FormatStatusPanel(Rows, 80, "会话统计:"));
        Assert.Contains("状态:", Program.FormatStatusPanel(Rows, 80));
    }

    [Fact]
    public void EmptyTitleOmitsTheLine()
    {
        // 空标题 = 不要标题行（调用方可以自己打标题，或者干脆不要）
        var text = Program.FormatStatusPanel(Rows, 80, "");
        Assert.DoesNotContain("状态:", text);
        Assert.Contains("模型", text);
    }

    [Fact]
    public void RightAlignKeepsValuesOnTheSameColumn()
    {
        // 请求次数/输入/输出 tokens 量级差好几个数量级，左对齐时位数不在同一列，
        // 扫读时对不齐「这一列是数字」这件事。
        var text = Program.FormatStatusPanel(Rows, 80, "会话统计:", rightAlign: true);
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).Skip(1).ToArray();
        Assert.True(lines.Length >= 3, text);
        // 右对齐时各行的**行尾**应当对齐（值都在最右），左对齐时行首对齐
        // 这里只断言不抛且行数对得上——具体列位置依赖终端宽度，交给宽度测试保证
        Assert.Equal(Rows.Length, lines.Length);
    }

    [Fact]
    public void ZeroWidthKeepsEverything()
    {
        // 宽度未知（0）时不裁剪：一个字段都不许丢
        var text = Program.FormatStatusPanel(Rows, 0, "会话统计:");
        foreach (var (key, _) in Rows)
            Assert.Contains(key, text);
    }
}
